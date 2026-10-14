namespace RevitGeoExport.Core
{
    public sealed class KmlResult
    {
        public KmlResult(int written, int omitted)
        {
            Written = written;
            Omitted = omitted;
        }

        /// <summary>Elements written to the file.</summary>
        public int Written { get; }

        /// <summary>Small elements left out to stay within <see cref="ExportOptions.MaxKmlPoints"/>.</summary>
        public int Omitted { get; }
    }
}
