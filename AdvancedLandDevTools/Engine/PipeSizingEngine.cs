using System;

namespace AdvancedLandDevTools.Engine
{
    // ═════════════════════════════════════════════════════════════════════════
    //  Pipe Sizing Calculator — Rational Method + Manning's Equation
    //  Pure math, no AutoCAD dependencies.
    // ═════════════════════════════════════════════════════════════════════════

    public class PipeSizingInput
    {
        // ── Hydrology (Demand) ────────────────────────────────────────────
        public double DrainageAreaSqFt  { get; set; }   // raw area in sq ft
        public double RunoffCoefficient { get; set; }   // c  (0.0 – 1.0)
        public double RainfallIntensity { get; set; }   // i  (in/hr)

        // ── Hydraulics (Supply) ───────────────────────────────────────────
        public double PipeDiameterIn    { get; set; }   // D  (inches)
        public double PipeSlope         { get; set; }   // S0 (dimensionless, e.g. 0.005)
        public double ManningsN         { get; set; }   // n  (e.g. 0.013)
    }

    public class PipeSizingResult
    {
        // ── Hydrology outputs ─────────────────────────────────────────────
        public double AreaAcres   { get; set; }
        public double QRunoff     { get; set; }   // CFS

        // ── Hydraulics outputs ────────────────────────────────────────────
        public double DiameterFt  { get; set; }
        public double AreaPipe    { get; set; }   // sq ft
        public double WettedP     { get; set; }   // ft
        public double HydRadius   { get; set; }   // ft
        public double QCapacity   { get; set; }   // CFS
        public double Velocity    { get; set; }   // ft/s

        // ── Validation ────────────────────────────────────────────────────
        public bool   CapacityPass  { get; set; }
        public string CapacityMsg   { get; set; } = "";
        public bool   VelocityPass  { get; set; }
        public string VelocityMsg   { get; set; } = "";

        public bool   IsValid       { get; set; }
        public string ErrorMessage  { get; set; } = "";
    }

    public static class PipeSizingEngine
    {
        private const double MIN_VELOCITY = 2.0;  // ft/s self-cleansing

        public static PipeSizingResult Calculate(PipeSizingInput input)
        {
            var r = new PipeSizingResult();

            // ── Input validation ──────────────────────────────────────────
            if (input.DrainageAreaSqFt <= 0)
            { r.ErrorMessage = "Drainage area must be greater than 0."; return r; }

            if (input.RunoffCoefficient <= 0 || input.RunoffCoefficient > 1.0)
            { r.ErrorMessage = "Runoff coefficient must be between 0.0 and 1.0."; return r; }

            if (input.RainfallIntensity <= 0)
            { r.ErrorMessage = "Rainfall intensity must be greater than 0."; return r; }

            if (input.PipeDiameterIn <= 0)
            { r.ErrorMessage = "Pipe diameter must be greater than 0."; return r; }

            if (input.PipeSlope <= 0)
            { r.ErrorMessage = "Pipe slope must be greater than 0."; return r; }

            if (input.ManningsN <= 0)
            { r.ErrorMessage = "Manning's n must be greater than 0."; return r; }

            // ══════════════════════════════════════════════════════════════
            //  PHASE 1 — HYDROLOGY (Demand)
            // ══════════════════════════════════════════════════════════════
            r.AreaAcres = input.DrainageAreaSqFt / 43560.0;
            r.QRunoff   = input.RunoffCoefficient * input.RainfallIntensity * r.AreaAcres;

            // ══════════════════════════════════════════════════════════════
            //  PHASE 2 — HYDRAULICS (Supply) — Manning's Equation
            // ══════════════════════════════════════════════════════════════
            r.DiameterFt = input.PipeDiameterIn / 12.0;
            r.AreaPipe   = Math.PI * Math.Pow(r.DiameterFt / 2.0, 2);
            r.WettedP    = Math.PI * r.DiameterFt;
            r.HydRadius  = r.AreaPipe / r.WettedP;

            r.QCapacity  = (1.49 / input.ManningsN)
                         * r.AreaPipe
                         * Math.Pow(r.HydRadius, 2.0 / 3.0)
                         * Math.Sqrt(input.PipeSlope);

            r.Velocity   = r.QCapacity / r.AreaPipe;

            // ══════════════════════════════════════════════════════════════
            //  PHASE 3 — VALIDATION
            // ══════════════════════════════════════════════════════════════
            r.CapacityPass = r.QCapacity >= r.QRunoff;
            r.CapacityMsg  = r.CapacityPass
                ? "PASS — Pipe capacity meets runoff demand"
                : "FAIL — Pipe too small, increase diameter or slope";

            r.VelocityPass = r.Velocity >= MIN_VELOCITY;
            r.VelocityMsg  = r.VelocityPass
                ? "PASS — Self-cleansing velocity met"
                : $"FAIL — Velocity {r.Velocity:F2} ft/s < {MIN_VELOCITY:F1} ft/s minimum";

            r.IsValid = true;
            return r;
        }

        /// <summary>
        /// Finds the minimum standard pipe diameter (inches) that passes both checks.
        /// </summary>
        public static int SuggestMinDiameter(PipeSizingInput input)
        {
            int[] standard = { 12, 15, 18, 24, 30, 36, 42, 48, 54, 60, 72, 84, 96 };

            foreach (int d in standard)
            {
                var test = new PipeSizingInput
                {
                    DrainageAreaSqFt  = input.DrainageAreaSqFt,
                    RunoffCoefficient = input.RunoffCoefficient,
                    RainfallIntensity = input.RainfallIntensity,
                    PipeDiameterIn    = d,
                    PipeSlope         = input.PipeSlope,
                    ManningsN         = input.ManningsN
                };

                var result = Calculate(test);
                if (result.IsValid && result.CapacityPass && result.VelocityPass)
                    return d;
            }

            return -1; // none of the standard sizes work
        }
    }
}
