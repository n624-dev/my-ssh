using MySsh.Core;
using Terminal.Gui;

namespace MySsh.App;

internal sealed partial class FileManagerWindow
{
    private void ChangePermissions()
    {
        var selected = SelectedRows(_activeLocal);
        if (selected.Count != 1)
        {
            MessageBox.Query(60, 7, "Permissions", "Select exactly one entry.", "OK");
            return;
        }
        var entry = selected[0];
        if (entry.Kind is not (EntryKind.File or EntryKind.Directory))
        {
            MessageBox.Query(70, 8, "Permissions", "Permissions cannot be changed on links or special files. Select the target explicitly.", "OK");
            return;
        }
        var fs = _activeLocal ? _local : _remote;
        if (entry.Mode is null || fs is not IPermissionFileSystem permissions)
        {
            MessageBox.Query(70, 8, "Permissions", "POSIX permission changes are not supported for this entry.", "OK");
            return;
        }
        var value = Prompt("Permissions", "Octal mode (000-777)", Convert.ToString(entry.Mode.Value & 0x1FF, 8).PadLeft(3, '0'));
        if (value is null) return;
        try
        {
            if (value.Length != 3 || value.Any(c => c is < '0' or > '7'))
                throw new IOException("Permission mode must be exactly three octal digits from 000 to 777.");
            var mode = Convert.ToUInt32(value, 8);
            UiFileOperation.Run("Change permissions", ct => permissions.SetPermissionsAsync(entry.Path, mode, ct));
            ReloadPane(_activeLocal);
        }
        catch (Exception ex) { ShowOperationError("Permissions", ex); }
    }
}
