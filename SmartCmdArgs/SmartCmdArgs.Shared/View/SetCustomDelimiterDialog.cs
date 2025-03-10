﻿using Microsoft.VisualStudio.PlatformUI;
using SmartCmdArgs.Helper;

namespace SmartCmdArgs.View
{
    class SetCustomDelimiterViewModel : PropertyChangedBase
    {
        public string Delimiter { get; set; }
        public string Postfix { get; set; }
        public string Prefix { get; set; }
    }

    class SetCustomDelimiterDialog : DialogWindow
    {
        private SetCustomDelimiterControl _control;

        public SetCustomDelimiterDialog(SetCustomDelimiterViewModel vm) : base()
        {
            ResizeMode = System.Windows.ResizeMode.NoResize;
            Width = 260;
            Height = 220;

            _control = new SetCustomDelimiterControl();
            _control.DataContext = vm;

            Title = "Smart Commandline Arguments HD Remix Delimiter Configuration";
            Content = _control;
        }
    }
}
