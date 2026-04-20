using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AdvancedLandDevTools.VehicleTracking.Core
{
    /// <summary>
    /// Represents a single rigid vehicle unit (car, truck body, trailer, etc.).
    /// All dimensions in feet, angles in radians unless noted.
    ///
    /// ── Steering-angle conventions (see Steering_Percentage research) ──
    ///   • MaxSteeringAngle = CENTERLINE (bicycle-model) angle used by the solver.
    ///   • MaxWheelAngle    = PHYSICAL inner-wheel angle as published on spec
    ///                        sheets. When non-zero this is the source of truth
    ///                        and the centerline is derived via Ackermann:
    ///                             R  = WB / tan(α_wheel) + T/2
    ///                             α_c = atan(WB / R)
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

        /// <summary>Center-to-center of left/right tire patches (ft).
        /// Used as a single track width unless FrontTrackWidth/RearTrackWidth
        /// are set non-zero.</summary>
        [JsonPropertyName("trackWidth")]
        public double TrackWidth { get; set; }

        /// <summary>Front-axle track width (ft). 0 → falls back to TrackWidth.</summary>
        [JsonPropertyName("frontTrackWidth")]
        public double FrontTrackWidth { get; set; }

        /// <summary>Rear-axle track width (ft). 0 → falls back to TrackWidth.</summary>
        [JsonPropertyName("rearTrackWidth")]
        public double RearTrackWidth { get; set; }

        /// <summary>Tire contact-patch width (ft). Used for wheel-rectangle rendering.</summary>
        [JsonPropertyName("tireWidth")]
        public double TireWidth { get; set; }

        /// <summary>Tire outer diameter (ft). Used for wheel-rectangle rendering.</summary>
        [JsonPropertyName("tireDiameter")]
        public double TireDiameter { get; set; }

        /// <summary>Maximum CENTERLINE (bicycle-model) steering angle (radians).
        /// This is what SweptPathSolver consumes.</summary>
        [JsonPropertyName("maxSteeringAngle")]
        public double MaxSteeringAngle { get; set; }

        /// <summary>Maximum PHYSICAL inner-wheel steer angle (radians). 0 → unknown
        /// (fall back to assuming MaxSteeringAngle ≈ wheel angle). When set, this
        /// is the source of truth and the centerline is derived via Ackermann.
        /// </summary>
        [JsonPropertyName("maxWheelAngle")]
        public double MaxWheelAngle { get; set; }

        /// <summary>Time to go from full-left to full-right (seconds) forward.</summary>
        [JsonPropertyName("lockToLockTime")]
        public double LockToLockTime { get; set; } = 4.0;

        /// <summary>Time to go from full-left to full-right (seconds) reverse.
        /// 0 → fall back to LockToLockTime.</summary>
        [JsonPropertyName("lockToLockTimeReverse")]
        public double LockToLockTimeReverse { get; set; }

        /// <summary>Minimum outside turning radius (ft). Derived if zero.</summary>
        [JsonPropertyName("minTurningRadius")]
        public double MinTurningRadius { get; set; }

        // ── Articulation (trailers, tillers, articulated buses) ──

        /// <summary>Maximum absolute articulation angle between this unit and the
        /// one it's coupled to (radians). 0 → no limit (free pintle).</summary>
        [JsonPropertyName("maxArticulationAngle")]
        public double MaxArticulationAngle { get; set; }

        /// <summary>Maximum steer angle allowed while the articulation is saturated
        /// (radians). Used by tillered aerials and articulated buses to prevent
        /// the jack-knife corner from over-rotating once the hinge hits its stop.
        /// 0 → same as MaxSteeringAngle.</summary>
        [JsonPropertyName("maxNonArticulatingSteeringAngle")]
        public double MaxNonArticulatingSteeringAngle { get; set; }

        // ── Self-steered / linked axles ──

        /// <summary>Indices of axles (0-based from front) that are linked via
        /// tandem/pusher/tag-axle steering. Linked axles steer proportionally
        /// to the primary steered axle — research §7.3.</summary>
        [JsonPropertyName("linkedAxles")]
        public int[] LinkedAxles { get; set; } = Array.Empty<int>();

        /// <summary>Friction resistance for self-steered (castering) axles in
        /// the 0..1 range.  0 = frictionless caster, 1 = locked. Controls how
        /// quickly a self-steered axle aligns with its velocity vector.</summary>
        [JsonPropertyName("selfSteeredFrictionFactor")]
        public double SelfSteeredFrictionFactor { get; set; }

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

        /// <summary>Effective front track width (falls back to TrackWidth).</summary>
        public double EffectiveFrontTrack =>
            FrontTrackWidth > 0 ? FrontTrackWidth : TrackWidth;

        /// <summary>Effective rear track width (falls back to TrackWidth).</summary>
        public double EffectiveRearTrack =>
            RearTrackWidth > 0 ? RearTrackWidth : TrackWidth;

        /// <summary>Effective reverse lock-to-lock time.</summary>
        public double EffectiveReverseLockToLockTime =>
            LockToLockTimeReverse > 0 ? LockToLockTimeReverse : LockToLockTime;

        // ── Ackermann helpers ─────────────────────────────────────────────

        /// <summary>
        /// Given a physical inner-wheel angle (rad), return the equivalent
        /// bicycle-model (centerline) angle. Research §4.1, §8.2.
        ///   R  = WB / tan(α_wheel_inner) + T/2
        ///   α_c = atan(WB / R)
        /// </summary>
        public static double WheelAngleToCenterline(double wheelAngleRad, double wheelbase, double track)
        {
            double a = Math.Abs(wheelAngleRad);
            if (a < 1e-9) return 0.0;
            double R = wheelbase / Math.Tan(a) + track * 0.5;
            double ac = Math.Atan(wheelbase / R);
            return Math.Sign(wheelAngleRad) * ac;
        }

        /// <summary>
        /// Given a centerline (bicycle) angle (rad), return the physical inner-
        /// and outer-wheel angles per Ackermann geometry. Research §4.1.
        ///   R_c     = WB / tan(α_c)
        ///   α_inner = atan(WB / (R_c − T/2))
        ///   α_outer = atan(WB / (R_c + T/2))
        /// </summary>
        public static (double inner, double outer) CenterlineToWheelAngles(double centerlineRad, double wheelbase, double track)
        {
            double a = Math.Abs(centerlineRad);
            if (a < 1e-9) return (0.0, 0.0);
            double Rc = wheelbase / Math.Tan(a);
            double halfT = track * 0.5;
            double inner = Math.Atan(wheelbase / Math.Max(Rc - halfT, 1e-6));
            double outer = Math.Atan(wheelbase / (Rc + halfT));
            int s = Math.Sign(centerlineRad);
            return (s * inner, s * outer);
        }

        /// <summary>
        /// Resolve the centerline MaxSteeringAngle the solver should use.
        /// Priority:
        ///   1. MaxWheelAngle (physical, from spec sheet) → Ackermann conversion
        ///   2. MaxSteeringAngle (already centerline)
        /// </summary>
        public double ResolvedCenterlineMaxSteer()
        {
            if (MaxWheelAngle > 1e-9)
                return WheelAngleToCenterline(MaxWheelAngle, Wheelbase, EffectiveFrontTrack);
            return MaxSteeringAngle;
        }
    }

    /// <summary>
    /// User-controllable limits applied on top of a vehicle's physical maxima.
    /// Research §6 & §9: operators often want to preview a turn at less than
    /// full lock to check clearance for a "comfortable" steer, to cap turn rate
    /// for low-speed articulated moves, or to bound by a target radius.
    /// A limit at the neutral value (1.0 for percentages, 0 for absolute caps)
    /// means "no additional limit beyond the vehicle's physical maximum".
    /// </summary>
    public class SteeringLimits
    {
        /// <summary>Fraction of physical max steering to allow (0..1). 1 = full lock.</summary>
        public double LimitSteeringToPercentage { get; set; } = 1.0;

        /// <summary>Absolute centerline-angle cap (rad). 0 = no cap.</summary>
        public double LimitToAngle { get; set; }

        /// <summary>Minimum turning radius to enforce (ft). 0 = no cap.
        /// Converted to a centerline angle via α = atan(WB / R).</summary>
        public double LimitToRadius { get; set; }

        /// <summary>Fraction of physical lock-to-lock rate to allow (0..1). 1 = full.</summary>
        public double LimitTurnRatePercent { get; set; } = 1.0;

        /// <summary>Combine all caps into an effective centerline max steer (rad).</summary>
        public double EffectiveMaxSteer(VehicleUnit v)
        {
            double physical = v.ResolvedCenterlineMaxSteer();
            double byPct    = physical * Math.Clamp(LimitSteeringToPercentage, 0.0, 1.0);
            double byAngle  = LimitToAngle > 1e-9 ? LimitToAngle : double.MaxValue;
            double byRadius = LimitToRadius > 1e-9
                ? Math.Atan(v.Wheelbase / LimitToRadius)
                : double.MaxValue;
            return Math.Min(byPct, Math.Min(byAngle, byRadius));
        }

        /// <summary>Effective lock-to-lock rate (rad/s) after the turn-rate cap.</summary>
        public double EffectiveMaxSteerRate(VehicleUnit v, bool reverse)
        {
            double llt = reverse ? v.EffectiveReverseLockToLockTime : v.LockToLockTime;
            double physRate = (2.0 * v.ResolvedCenterlineMaxSteer()) / Math.Max(llt, 1e-3);
            return physRate * Math.Clamp(LimitTurnRatePercent, 0.0, 1.0);
        }
    }

    /// <summary>
    /// Live steering telemetry — the "steering percentage" surface the research
    /// document describes. Vehicle-independent: 0 % = straight, 100 % = the
    /// effective (possibly user-limited) max.
    /// </summary>
    public class SteeringState
    {
        /// <summary>Current centerline steer angle (rad).</summary>
        public double CurrentAngle { get; set; }

        /// <summary>Effective max centerline angle (rad) — physical max reduced by limits.</summary>
        public double EffectiveMaxAngle { get; set; }

        /// <summary>Effective max steer rate (rad/s).</summary>
        public double EffectiveMaxRate { get; set; }

        /// <summary>CurrentAngle ÷ EffectiveMaxAngle × 100. Vehicle-independent.</summary>
        public double SteeringPercentage =>
            EffectiveMaxAngle > 1e-9
                ? (CurrentAngle / EffectiveMaxAngle) * 100.0
                : 0.0;

        /// <summary>Current radius implied by the bicycle model (ft). +∞ when straight.</summary>
        public double CurrentRadius(double wheelbase) =>
            Math.Abs(CurrentAngle) > 1e-9
                ? wheelbase / Math.Tan(Math.Abs(CurrentAngle))
                : double.PositiveInfinity;
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
