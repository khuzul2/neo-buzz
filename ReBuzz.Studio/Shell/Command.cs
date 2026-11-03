// Fork (docs/specs/studio-mvp.md): a plain ICommand around an action, for menus and key bindings.

using System;
using System.Windows.Input;

namespace ReBuzz.Studio.Shell
{
    internal sealed class Command : ICommand
    {
        readonly Action run;
        readonly Func<bool> can;

        public Command(Action run, Func<bool> can = null)
        {
            this.run = run;
            this.can = can;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) => can?.Invoke() ?? true;

        public void Execute(object parameter)
        {
            if (CanExecute(parameter)) run();
        }

        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
