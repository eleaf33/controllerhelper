namespace ControllerHelper.Services;

internal sealed class OverlayNavigation
{
    private int _selection = 2;
    private int _lastTopSelection = 2;

    internal int Selection => _selection;

    internal void Select(int selection)
    {
        _selection = selection;
        if (selection < 4) _lastTopSelection = selection;
    }

    internal int MoveHorizontal(int direction, bool primaryEnabled, bool monitorEnabled)
    {
        int selection = _selection;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            selection = Math.Clamp(selection + direction, 0, 3);
            if (Enabled(selection, primaryEnabled, monitorEnabled)) break;
        }
        return Enabled(selection, primaryEnabled, monitorEnabled) ? selection : _selection;
    }

    internal int MoveVertical(int direction, bool primaryEnabled, bool monitorEnabled)
    {
        if (direction > 0 && _selection != 4) return 4;
        if (direction < 0 && _selection == 4)
            return _lastTopSelection == 0 && primaryEnabled ? 0 :
                _lastTopSelection == 1 && monitorEnabled ? 1 : 2;
        return _selection;
    }

    private static bool Enabled(int selection, bool primaryEnabled, bool monitorEnabled) =>
        selection == 0 ? primaryEnabled : selection == 1 ? monitorEnabled : true;
}
