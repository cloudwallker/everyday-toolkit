using System.Runtime.InteropServices;

internal static class ClipboardTestCleanup
{
    internal static bool TryRestore(Action restore,Action retryPause,Action onBusy,int maxAttempts=10)
    {
        if(maxAttempts<1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        for(int attempt=0;attempt<maxAttempts;attempt++)
        {
            try { restore(); return true; }
            catch(ExternalException)
            {
                onBusy();
                if(attempt+1<maxAttempts) retryPause();
            }
        }
        return false;
    }
}
