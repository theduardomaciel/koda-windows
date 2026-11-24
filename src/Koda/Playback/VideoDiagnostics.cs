namespace Koda.Playback;

internal sealed record VideoDiagnostics(string VideoId, uint Width, uint Height, string Format,
    double SourceFps, string State, double Position, double Duration, double Speed,
    double BufferFill, long StreamBitrate, SmoothMotionDiagnostics Motion);
