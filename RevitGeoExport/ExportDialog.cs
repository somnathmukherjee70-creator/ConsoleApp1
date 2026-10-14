using System;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using RevitGeoExport.Core;

namespace RevitGeoExport
{
    public sealed class ExportSettings
    {
        /// <summary>When set, the model is placed by its own shared coordinates in this system and the latitude/longitude anchor is ignored.</summary>
        public ProjectedCrs Crs { get; set; }

        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public AnchorPoint Anchor { get; set; }
        public double RotationDegrees { get; set; }
        public AltitudeMode AltitudeMode { get; set; }
        public double SiteElevationMeters { get; set; }
        public double OffsetMeters { get; set; }
        public bool WriteKmz { get; set; } = true;
        public bool WriteGeoJson { get; set; } = true;
        public bool WriteWebMap { get; set; } = true;
        public bool SaveLocationToProject { get; set; }

        /// <summary>Output path without extension; each format adds its own.</summary>
        public string OutputBasePath { get; set; }
    }

    /// <summary>Collects where to place the model and what to export.</summary>
    public sealed class ExportDialog : Form
    {
        private readonly TextBox _crs = new TextBox { Width = 260 };
        private readonly Label _crsStatus = Hint("");
        private readonly TextBox _coordinates = new TextBox { Width = 260 };
        private readonly ComboBox _anchor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly NumericUpDown _rotation = Number(-360, 360, 2);
        private readonly ComboBox _altitude = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        private readonly NumericUpDown _elevation = Number(-500, 9000, 2);
        private readonly NumericUpDown _offset = Number(-1000, 1000, 2);
        private readonly CheckBox _kmz = new CheckBox { Text = "Google Earth (.kmz)", AutoSize = true, Checked = true };
        private readonly CheckBox _geoJson = new CheckBox { Text = "GeoJSON footprints (.geojson)", AutoSize = true, Checked = true };
        private readonly CheckBox _webMap = new CheckBox { Text = "Live web map (.html)", AutoSize = true, Checked = true };
        private readonly CheckBox _save = new CheckBox { Text = "Save these coordinates to the Revit project location", AutoSize = true };
        private readonly TextBox _output = new TextBox { Width = 260 };

        public ExportDialog(RevitLocation location, string defaultOutput)
        {
            Text = "Place model on map";
            using (var icon = App.OpenResource("logo.ico"))
                Icon = new System.Drawing.Icon(icon);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);

            _crs.Text = location.DetectedCrs?.Epsg.ToString(CultureInfo.InvariantCulture) ?? "";
            _crs.TextChanged += (s, e) => UpdatePlacementMode(location.DetectionNote);
            _coordinates.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0000000}, {1:0.0000000}", location.LatitudeDegrees, location.LongitudeDegrees);
            _anchor.Items.AddRange(new object[] { "Survey Point", "Project Base Point", "Internal Origin" });
            _anchor.SelectedIndex = 0;
            _altitude.Items.AddRange(new object[] { "Sit on the terrain", "Above sea level" });
            _altitude.SelectedIndex = 0;
            // Shared coordinates in a survey system usually carry real elevations already.
            _elevation.Value = location.DetectedCrs != null ? 0 : (decimal)Math.Max(-500, Math.Min(9000, location.ElevationMeters));
            _output.Text = defaultOutput;

            var browse = new Button { Text = "Browse…", AutoSize = true };
            browse.Click += (s, e) => Browse();

            var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            AddRow(grid, "Coordinate system (EPSG)", _crs);
            AddRow(grid, "", _crsStatus);
            AddRow(grid, "Latitude, longitude", _coordinates);
            AddRow(grid, "", Hint($"Revit location: {location.PlaceName}. Paste from Google Maps (right-click a spot › copy the coordinates)."));
            AddRow(grid, "Coordinates are of the", _anchor);
            AddRow(grid, "Rotation adjustment (° clockwise)", _rotation);
            AddRow(grid, "Height", _altitude);
            AddRow(grid, "Height of Z = 0 above sea (m)", _elevation);
            AddRow(grid, "Vertical offset (m)", _offset);
            AddRow(grid, "Export", Stack(_kmz, _geoJson, _webMap));
            AddRow(grid, "Save to (no extension)", Stack(_output, browse));
            AddRow(grid, "", _save);

            var ok = new Button { Text = "Export", DialogResult = DialogResult.OK, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            ok.Click += OnExport;
            AcceptButton = ok;
            CancelButton = cancel;
            AddRow(grid, "", Stack(ok, cancel));

            Controls.Add(grid);
            UpdatePlacementMode(location.DetectionNote);
        }

        private bool UsesCoordinateSystem => !string.IsNullOrWhiteSpace(_crs.Text);

        /// <summary>With a coordinate system the model places itself, so the manual placement fields are disabled.</summary>
        private void UpdatePlacementMode(string detectionNote)
        {
            bool parsed = ProjectedCrs.TryParse(_crs.Text, out var crs);
            _crsStatus.Text = !UsesCoordinateSystem ? detectionNote
                : parsed ? $"Placed automatically by the model's shared coordinates in {crs}."
                : "Not a supported coordinate system (UTM on WGS84/NAD83/ETRS89/GDA, Dubai DLTM 3997, Pennsylvania State Plane 2271/2272/32128/32129, or 3857).";
            _crsStatus.ForeColor = UsesCoordinateSystem && !parsed ? System.Drawing.Color.Firebrick : System.Drawing.SystemColors.GrayText;

            _coordinates.Enabled = _anchor.Enabled = _rotation.Enabled = _save.Enabled = !UsesCoordinateSystem;
            if (UsesCoordinateSystem)
                _save.Checked = false;
        }

        public ExportSettings Settings { get; private set; }

        private void OnExport(object sender, EventArgs e)
        {
            ProjectedCrs crs = null;
            if (UsesCoordinateSystem && !ProjectedCrs.TryParse(_crs.Text, out crs))
            {
                MessageBox.Show(this, "The coordinate system isn't supported. Clear it to place the model by latitude/longitude instead.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            double lat = 0, lon = 0;
            if (crs == null && !TryParseCoordinates(_coordinates.Text, out lat, out lon))
            {
                MessageBox.Show(this, "Enter the coordinates as \"latitude, longitude\" in decimal degrees, e.g. 22.544808, 88.342558.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            if (!(_kmz.Checked || _geoJson.Checked || _webMap.Checked) || string.IsNullOrWhiteSpace(_output.Text))
            {
                MessageBox.Show(this, "Pick at least one format and an output location.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            Settings = new ExportSettings
            {
                Crs = crs,
                Latitude = lat,
                Longitude = lon,
                Anchor = (AnchorPoint)_anchor.SelectedIndex,
                RotationDegrees = (double)_rotation.Value,
                AltitudeMode = (AltitudeMode)_altitude.SelectedIndex,
                SiteElevationMeters = (double)_elevation.Value,
                OffsetMeters = (double)_offset.Value,
                WriteKmz = _kmz.Checked,
                WriteGeoJson = _geoJson.Checked,
                WriteWebMap = _webMap.Checked,
                SaveLocationToProject = _save.Checked,
                OutputBasePath = StripExtension(_output.Text.Trim()),
            };
        }

        internal static bool TryParseCoordinates(string text, out double latitude, out double longitude)
        {
            latitude = longitude = 0;
            var parts = (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out latitude)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out longitude)
                && Math.Abs(latitude) <= 90 && Math.Abs(longitude) <= 180;
        }

        private void Browse()
        {
            using (var dialog = new SaveFileDialog { Filter = "All exports|*.*", FileName = Path.GetFileName(_output.Text) })
            {
                var folder = Path.GetDirectoryName(_output.Text);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    dialog.InitialDirectory = folder;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    _output.Text = StripExtension(dialog.FileName);
            }
        }

        private static string StripExtension(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".kmz" || ext == ".kml" || ext == ".geojson" || ext == ".html"
                ? Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path))
                : path;
        }

        private static NumericUpDown Number(int min, int max, int decimals) =>
            new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Width = 100 };

        private static Label Hint(string text) =>
            new Label { Text = text, AutoSize = true, MaximumSize = new System.Drawing.Size(280, 0), ForeColor = System.Drawing.SystemColors.GrayText };

        private static FlowLayoutPanel Stack(params Control[] controls)
        {
            var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = controls[0] is CheckBox ? FlowDirection.TopDown : FlowDirection.LeftToRight };
            panel.Controls.AddRange(controls);
            return panel;
        }

        private static void AddRow(TableLayoutPanel grid, string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) });
            grid.Controls.Add(control);
        }
    }
}
