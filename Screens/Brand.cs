using VRageMath;

namespace Conduit
{
    // Cosmetic skin matching the Formidan/Shipyard console look. Flavor + palette in one place.
    internal static class Brand
    {
        public const string Faction = "FORMIDAN MANDATE";
        public const string Product = "CONDUIT  //  DATA UPLINK";
        public const string Classified =
            "PROPRIETARY  ||  PROPERTY OF THE FORMIDAN MANDATE  ||  DATA IN TRANSIT IS A CONTROLLED ASSET";

        // Amber/steel megacorp palette.
        public static readonly Vector4 Accent    = new Vector4(0.95f, 0.62f, 0.12f, 1f);  // amber/gold
        public static readonly Vector4 AccentDim = new Vector4(0.70f, 0.50f, 0.22f, 1f);
        public static readonly Vector4 Warn       = new Vector4(0.95f, 0.32f, 0.22f, 1f);  // alert red
        public static readonly Vector4 Ok         = new Vector4(0.55f, 0.95f, 0.55f, 1f);  // green
        public static readonly Vector4 Muted      = new Vector4(0.62f, 0.62f, 0.66f, 1f);
        public static readonly Vector4 Bg         = new Vector4(0.035f, 0.04f, 0.05f, 1f);   // fully opaque
    }
}
