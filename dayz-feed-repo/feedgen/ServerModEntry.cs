using System.Text.Json.Serialization;

namespace DayZLauncher.Models
{
    /// <summary>
    /// One required mod as shown in a server's detail drawer: workshop id, display name, preview
    /// image, and whether it is already installed locally.
    ///
    /// The drawer used to list bare "Mod ID: 2116157322" rows, which told you nothing about what
    /// a server actually runs.
    /// </summary>
    public class ServerModEntry : ObservableModel
    {
        private string _name = string.Empty;
        private string _localThumbnailPath = string.Empty;
        private bool _isInstalled;

        public string WorkshopId { get; set; } = string.Empty;

        /// <summary>
        /// Display name. Falls back to "Workshop Mod #id" until a name is resolved, so the row is
        /// never blank.
        /// </summary>
        public string Name
        {
            get => string.IsNullOrWhiteSpace(_name) ? $"Workshop Mod #{WorkshopId}" : _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>True once a real name has been resolved, as opposed to the id placeholder.</summary>
        [JsonIgnore]
        public bool HasResolvedName => !string.IsNullOrWhiteSpace(_name);

        public string LocalThumbnailPath
        {
            get => _localThumbnailPath;
            set => SetProperty(ref _localThumbnailPath, value);
        }

        /// <summary>Whether this mod is already in the local library.</summary>
        public bool IsInstalled
        {
            get => _isInstalled;
            set
            {
                if (SetProperty(ref _isInstalled, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                }
            }
        }

        public string StatusText => IsInstalled ? "Installed" : "Missing";

        public string WorkshopUrl => $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopId}";
    }
}
