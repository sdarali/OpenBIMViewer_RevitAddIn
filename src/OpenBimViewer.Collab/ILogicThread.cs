using System;

namespace OpenBimViewer.Collab
{
    /// <summary>
    /// The one thread the Core is used from. Every bridge call and every callback into it happens
    /// here. In Revit this is the UI thread (through its dispatcher), which is also where the
    /// panel's code runs.
    /// </summary>
    public interface ILogicThread
    {
        /// <summary>Run <paramref name="action"/> later on the logic thread (never inline).</summary>
        void Post(Action action);

        /// <summary>Run <paramref name="action"/> once after <paramref name="delay"/>; dispose to cancel.</summary>
        IDisposable Delay(TimeSpan delay, Action action);
    }
}
