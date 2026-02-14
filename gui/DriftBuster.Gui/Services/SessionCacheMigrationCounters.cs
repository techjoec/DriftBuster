using System.Threading;

namespace DriftBuster.Gui.Services
{
    internal static class SessionCacheMigrationCounters
    {
        private static int _migrationSuccessCount;
        private static int _migrationFailureCount;

        public static int Successes => Volatile.Read(ref _migrationSuccessCount);

        public static int Failures => Volatile.Read(ref _migrationFailureCount);

        public static void RecordSuccess()
        {
            Interlocked.Increment(ref _migrationSuccessCount);
        }

        public static void RecordFailure()
        {
            Interlocked.Increment(ref _migrationFailureCount);
        }

        public static void Reset()
        {
            Volatile.Write(ref _migrationSuccessCount, 0);
            Volatile.Write(ref _migrationFailureCount, 0);
        }
    }
}
