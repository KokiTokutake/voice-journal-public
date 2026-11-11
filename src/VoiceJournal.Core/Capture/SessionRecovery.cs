using System.Globalization;
using System.Security.Cryptography;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Sessions;

namespace VoiceJournal.Core.Capture;

/// <summary>What startup recovery did to one session.</summary>
/// <param name="RecoveredChunks">Partial chunks copied into <c>.recovered.wav</c> files.</param>
/// <param name="AdoptedChunks">Chunks already renamed to <c>.wav</c> but not yet recorded as completed.</param>
/// <param name="InconsistentChunks">Recovered chunks whose file held fewer frames than the sidecar confirmed.</param>
public sealed record SessionRecoveryResult(Guid SessionId, CaptureState NewState, int RecoveredChunks, int AdoptedChunks, int InconsistentChunks);

/// <summary>
/// Startup recovery of sessions left in Recording / Partial / Stopping (detail spec 19, REC-001).
/// Never modifies or deletes the original <c>.partial</c> files; recovered audio goes to new files.
/// </summary>
public static class SessionRecovery
{
    /// <summary>True when recording was still in progress, or a failed write left an unfinished chunk.</summary>
    public static bool NeedsRecovery(SessionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.CaptureState is CaptureState.Ready or CaptureState.Recording or CaptureState.Partial or CaptureState.Stopping
            || manifest.Tracks.Any(t => t.Chunks.Any(c => c.State == ChunkState.Partial));
    }

    public static SessionRecoveryResult Recover(string sessionDirectory, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var manifest = SessionManifest.Load(sessionDirectory);
        if (!NeedsRecovery(manifest)) return new SessionRecoveryResult(manifest.SessionId, manifest.CaptureState, 0, 0, 0);

        int recovered = 0, adopted = 0, inconsistent = 0;
        foreach (var track in manifest.Tracks)
        {
            var lastMs = track.StartOffsetMs;
            foreach (var chunk in track.Chunks.Where(c => c.State == ChunkState.Partial).ToList())
            {
                var finalRelative = SessionPaths.ChunkFile(track.Source, chunk.Sequence);
                var finalPath = SessionPaths.Resolve(sessionDirectory, finalRelative);
                var partialPath = SessionPaths.Resolve(sessionDirectory, SessionPaths.PartialChunkFile(track.Source, chunk.Sequence));

                if (!File.Exists(partialPath) && File.Exists(finalPath))
                {
                    // Crashed after the rename but before the manifest was saved: the .wav is complete.
                    AdoptCompleted(chunk, finalRelative, finalPath);
                    adopted++;
                }
                else if (File.Exists(partialPath))
                {
                    var recoveredRelative = SessionPaths.RecoveredChunkFile(track.Source, chunk.Sequence);
                    var result = PartialChunkRecovery.Recover(partialPath, SessionPaths.Resolve(sessionDirectory, recoveredRelative));
                    chunk.File = recoveredRelative;
                    chunk.FrameCount = result.RecoveredFrames;
                    chunk.SizeBytes = result.SizeBytes;
                    chunk.Sha256 = result.Sha256;
                    chunk.State = ChunkState.Recovered;
                    track.Events.Add(new AudioEventManifest
                    {
                        Kind = AudioEventKind.Recovered,
                        StartMs = chunk.StartMs,
                        EndMs = chunk.StartMs + result.RecoveredFrames * 1000 / track.Format.SampleRate,
                        Detail = string.Create(CultureInfo.InvariantCulture, $"sequence={chunk.Sequence}; consistent={result.ConsistentWithSidecar}; dropped_tail_bytes={result.DroppedTailBytes}"),
                    });
                    recovered++;
                    if (!result.ConsistentWithSidecar) inconsistent++;
                }
                else
                {
                    // Neither file exists (crash before the first header write): nothing to keep.
                    track.Chunks.Remove(chunk);
                    continue;
                }
                lastMs = Math.Max(lastMs, chunk.StartMs + chunk.FrameCount * 1000 / track.Format.SampleRate);
            }

            foreach (var c in track.Chunks) lastMs = Math.Max(lastMs, c.StartMs + c.FrameCount * 1000 / track.Format.SampleRate);
            if (track.State == TrackState.Recording) track.State = TrackState.Stopped;
            track.Events.Add(new AudioEventManifest { Kind = AudioEventKind.Interrupted, StartMs = lastMs, Detail = "recording ended unexpectedly; the last chunk may be missing up to 5 minutes" });
        }

        var hasAudio = manifest.Tracks.Any(t => t.Chunks.Count > 0);
        manifest.CaptureState = manifest.CaptureState == CaptureState.Ready || !hasAudio
            ? CaptureStateMachine.Transition(manifest.CaptureState == CaptureState.Ready ? CaptureState.Ready : CaptureState.Recording, CaptureState.Failed)
            : CaptureState.Interrupted;
        manifest.EndedAtUtc ??= time.GetUtcNow();
        manifest.Save(sessionDirectory);
        return new SessionRecoveryResult(manifest.SessionId, manifest.CaptureState, recovered, adopted, inconsistent);
    }

    private static void AdoptCompleted(ChunkManifest chunk, string relative, string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = WavFile.ReadHeader(fs);
        fs.Position = 0;
        chunk.File = relative;
        chunk.FrameCount = info.DeclaredDataSize / info.Format.BlockAlign;
        chunk.SizeBytes = fs.Length;
        chunk.Sha256 = Convert.ToHexStringLower(SHA256.HashData(fs));
        chunk.State = ChunkState.Completed;
    }
}
