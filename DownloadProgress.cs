using System.Text.RegularExpressions;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace YT_DLP_UI;

public sealed partial class HomePage
{
    public class DownloadProgress
    {
        public double Percentage = 0.0;
        public int PlaylistItem = 1;
        public int PlaylistTotal = 1;
        private double ExtractedProgress = 0.0;

        public void ExtractPercentage(string line)
        {
            // Looks for a Percentage in the format: [download]  42.3% ...
            Regex regex = DownloadPercentageRegex();
            Match match = regex.Match(line);
            if (!match.Success) return;

            string numeric = match.Groups[1].Value.Replace(',', '.');
            if (!double.TryParse(numeric, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double newPercentage)) return;
            if (newPercentage > 100) return;
            Percentage = newPercentage;
        }

        public void ExtractPlaylistItems(string line)
        {
            // Looks for playlist item/total in the format: [download] Downloading 1 of 8
            int prevPlaylistItem = PlaylistItem;
            Regex regex = PlaylistItemsRegex();
            Match match = regex.Match(line);
            if (!match.Success) return;
            if (!int.TryParse(match.Groups[1].Value, out PlaylistItem)) return;
            if (!int.TryParse(match.Groups[2].Value, out PlaylistTotal)) return;
            if (PlaylistItem > prevPlaylistItem) Percentage = 0;
        }

        public double ExtractProgress(string line)
        {
            ExtractPercentage(line);
            ExtractPlaylistItems(line);
            double newProgress = (PlaylistItem - 1 + Percentage / 100) / PlaylistTotal * 100;
            ExtractedProgress = newProgress;
            return ExtractedProgress;
        }
    }
}
