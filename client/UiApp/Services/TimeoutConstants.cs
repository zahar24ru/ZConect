namespace UiApp.Services;

/// <summary>Centralized timeout and retry constants.</summary>
internal static class TimeoutConstants
{
    public const int PipeConnectTimeoutMs = 2000;
    public const int PipeReconnectDelayMs = 5000;
    public const int CallerDeferredWaitMs = 1500;
}
