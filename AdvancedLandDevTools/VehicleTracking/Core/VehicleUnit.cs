using System;
using System.Text.Json.Serialization;

namespace AdvancedLandDevTools.VehicleTracking.Core
{
    /// <summary>
    /// Represents a single rigid vehicle unit (car, truck body, trailer, etc.).
    /// All dimensions in feet, angles in radians unless noted.
    /// </summary>
    public class VehicleUnit
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";

        /// <summary>Overall length bumper-to-bumper (ft).</summary>
        [JsonPropertyName("length")]
        public double Length { get; set; }

        /// <summary>Overall body width (ft).</summary>
        [JsonPropertyName("width")]
        public double Width { get; set; }

        /// <summary>Front axle center to rear axle center (ft).</summary>
        [JsonPropertyName("wheelbase")]
        public double Wheelbase { get; set; }

        /// <summary>Front bumper to front axle center (ft).</summary>
        [JsonPropertyName("frontOverhang")]
        public double FrontOverhang { get; set; }

        /// <summary>Rear axle center to rear bumper (ft).</summary>
        [JsonPropertyName("rearOverhang")]
        public double RearOverhang { get; set; }

        /// <summary>Center-to-center of left/right tire patches (ft).</summary>
        [JsonPropertyName("trackWidth")]
        public double TrackWidth { get; set; }

        /// <summary>Maximum steering angle from straight-ahead (radians).</summary>
        [JsonPropertyName("maxSteeringAngle")]
        public double MaxSteeringAngle { get; set; }

        /// <summary>Time to go from full-left to full-right (seconds).</summary>
        [JsonPropertyName("lockToLockTime")]
        public double LockToLockTime { get; set; } = 4.0;

        /// <summary>Minimum outside turning radius (ft). Derived if zero.</summary>
        [JsonPropertyName("minTurningRadius")]
        public double MinTurningRadius { get; set; }

        /// <summary>Category tag for filtering (e.g. "Passenger", "Single Unit", "Semi").</summary>
        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        /// <summary>Whether this is a Florida-specific vehicle variant.</summary>
        [JsonPropertyName("isFloridaVehicle")]
        public bool IsFloridaVehicle { get; set; }

        /// <summary>Compute min turning radius from wheelbase + max steering angle.</summary>
        public double ComputedMinRadius =>
            MaxSteeringAngle > 1e-9
                ? Wheelbase / Math.Tan(MaxSteeringAngle)
                : double.MaxValue;

        /// <summary>Effective minimum turning radius (user-set or computed).</summary>
        public double EffectiveMinRadius =>
            MinTurningRadius > 0 ? MinTurningRadius : ComputedMinRadius;
    }

    /// <summary>Coupling type between articulated vehicle units.</summary>
    public enum CouplingType
    {
        FifthWheel,   // tractor-to-semitrailer
        Pintle,       // truck-to-full-trailer
        Drawbar       // dolly coupling
    }

    /// <summary>
    /// Defines how two vehicle units connect.
    /// </summary>
    public class CouplingDef
    {
        /// <summary>Type of hitch/coupling.</summary>
        [JsonPropertyName("type")]
        public CouplingType Type { get; set; }

        /// <summary>Distance from the leading unit's rear axle to the coupling point (ft).</summary>
        [JsonPropertyName("hitchOffset")]
        public double HitchOffset { get; set; }

        /// <summary>Distance from the trailing unit's front to the coupling point (ft).
        /// For a semitrailer this is the kingpin setback.</summary>
        [JsonPropertyName("kingpinOffset")]
        public double KingpinOffset { get; set; }
    }

    /// <summary>
    /// An articulated vehicle: a lead unit plus one or more trailing units with couplings.
    /// </summary>
    public class ArticulatedVehicle
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("symbol")]
        public string Symbol { get; set; } = "";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "";

        [JsonPropertyName("isFloridaVehicle")]
        public bool IsFloridaVehicle { get; set; }

        [JsonPropertyName("leadUnit")]
        public VehicleUnit LeadUnit { get; set; } = new();

        [JsonPropertyName("trailers")]
        public TrailerDef[] Trailers { get; set; } = Array.Empty<TrailerDef>();

        /// <summary>Total length bumper-to-bumper of entire combination.</summary>
        [JsonPropertyName("totalLength")]
        public double TotalLength { get; set; }
    }

    /// <summary>A trailer unit with its coupling to the preceding unit.</summary>
    public class TrailerDef
    {
        [JsonPropertyName("unit")]
        public VehicleUnit Unit { get; set; } = new();

        [JsonPropertyName("coupling")]
        public CouplingDef Coupling { get; set; } = new();
    }
}
