using System;
using System.Windows;

namespace Configurator;

public partial class MainWindow
{
    private void ConfigureMode4Editor(string availableProject)
    {
        if (_isOfflineWorkspace && _openedLocalProject != null)
            FunctionalBlockEditorControl.ConfigureOffline(_openedLocalProject);
        else
            FunctionalBlockEditorControl.ConfigureOnline(_connection?.Server ?? string.Empty, availableProject);
    }

    private void FunctionalBlockEditorControl_ProjectDataChanged(string description)
    {
        ShowLastAction(description);
        if (_isOfflineWorkspace) _offlineProjectDirty = true;
    }
}
