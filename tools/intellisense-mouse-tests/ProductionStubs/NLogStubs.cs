using System;

// The test project compiles the real CompletionListWindow.xaml.cs, which logs through NLog.
// Providing a minimal NLog shim keeps the test suite self-contained: it needs no main-project
// Release build and no NuGet restore. Logging is a side channel - it does not affect the
// behaviour under test - so a silent implementation is sufficient.

namespace NLog
{
    public interface ILogger
    {
        void Debug(string message, params object[] args);
        void Info(string message, params object[] args);
        void Warn(string message, params object[] args);
        void Warn(Exception exception, string message, params object[] args);
        void Error(string message, params object[] args);
        void Error(Exception exception, string message, params object[] args);
    }

    internal sealed class SilentLogger : ILogger
    {
        public void Debug(string message, params object[] args) { }
        public void Info(string message, params object[] args) { }
        public void Warn(string message, params object[] args) { }
        public void Warn(Exception exception, string message, params object[] args) { }
        public void Error(string message, params object[] args) { }
        public void Error(Exception exception, string message, params object[] args) { }
    }

    public static class LogManager
    {
        private static readonly ILogger Instance = new SilentLogger();

        public static ILogger GetCurrentClassLogger()
        {
            return Instance;
        }
    }
}
