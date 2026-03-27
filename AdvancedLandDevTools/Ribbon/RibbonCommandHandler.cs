using System;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;

namespace AdvancedLandDevTools.Ribbon
{
    /// <summary>
    /// ICommand implementation that sends an AutoCAD command string
    /// to the active document when a Ribbon button is clicked.
    /// </summary>
    public class RibbonCommandHandler : ICommand
    {
        private readonly string _commandString;

        /// <param name="commandString">
        /// The AutoCAD command string, e.g. "BULKSUR " (trailing space = Enter).
        /// </param>
        public RibbonCommandHandler(string commandString)
        {
            _commandString = commandString;
        }

        // Always enabled while a document is open
        public bool CanExecute(object? parameter)
            => Application.DocumentManager.MdiActiveDocument != null;

        public event EventHandler? CanExecuteChanged
        {
            add    { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public void Execute(object? parameter)
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            // SendStringToExecute posts the command asynchronously to the
            // AutoCAD message pump – safe to call from any Ribbon handler.
            doc.SendStringToExecute(_commandString, true, false, false);
        }
    }
}
