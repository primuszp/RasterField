using Avalonia.Controls;
using Avalonia.Layout;

namespace RasterField
{
    internal static class DialogControls
    {
        public static StackPanel ButtonRow(params Control[] buttons)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            foreach (var button in buttons) row.Children.Add(button);
            return row;
        }
    }
}
