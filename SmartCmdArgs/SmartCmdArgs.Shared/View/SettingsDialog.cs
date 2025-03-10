﻿using SmartCmdArgs.ViewModel;
using Microsoft.VisualStudio.PlatformUI;

namespace SmartCmdArgs.View
{
    class SettingsDialog : DialogWindow
    {
        private SettingsControl _settingsControl;

        public SettingsDialog(SettingsViewModel settingsViewModel) : base()
        {
            MinWidth = 600;
            MinHeight = 350;

            Width = 670;
            Height = 540;

            _settingsControl = new SettingsControl();
            _settingsControl.DataContext = settingsViewModel;

            Title = "Smart Commandline Arguments HD Remix Settings";
            Content = _settingsControl;
        }
    }
}
