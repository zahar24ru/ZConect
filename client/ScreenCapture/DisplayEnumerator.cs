using System.Windows.Forms;

namespace ScreenCapture;

public static class DisplayEnumerator
{
    public static IReadOnlyList<DisplaySource> GetDisplays()
    {
        return Screen.AllScreens
            .Select((s, idx) => new DisplaySource
            {
                Id = $"DISPLAY{idx + 1}",
                Name = s.DeviceName,
                X = s.Bounds.X,
                Y = s.Bounds.Y,
                Width = s.Bounds.Width,
                Height = s.Bounds.Height,
                IsPrimary = s.Primary
            })
            .ToList();
    }
}
