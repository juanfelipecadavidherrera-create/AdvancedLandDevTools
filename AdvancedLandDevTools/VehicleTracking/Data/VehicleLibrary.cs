using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AdvancedLandDevTools.VehicleTracking.Core;

namespace AdvancedLandDevTools.VehicleTracking.Data
{
    /// <summary>
    /// Manages the vehicle library — AASHTO defaults + user custom vehicles.
    /// Persists custom vehicles to JSON in %APPDATA%\AdvancedLandDevTools\vehicles\.
    /// </summary>
    public static class VehicleLibrary
    {
        private static readonly string _customDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AdvancedLandDevTools", "vehicles");

        private static List<VehicleUnit>? _singleUnits;
        private static List<ArticulatedVehicle>? _articulatedVehicles;

        /// <summary>All available single-unit vehicles.</summary>
        public static List<VehicleUnit> SingleUnits
        {
            get
            {
                if (_singleUnits == null) LoadAll();
                return _singleUnits!;
            }
        }

        /// <summary>All available articulated vehicles.</summary>
        public static List<ArticulatedVehicle> ArticulatedVehicles
        {
            get
            {
                if (_articulatedVehicles == null) LoadAll();
                return _articulatedVehicles!;
            }
        }

        /// <summary>All vehicles as a unified display list.</summary>
        public static List<(string Name, string Symbol, string Category, bool IsArticulated, int Index)> GetDisplayList()
        {
            var list = new List<(string, string, string, bool, int)>();
            for (int i = 0; i < SingleUnits.Count; i++)
            {
                var v = SingleUnits[i];
                list.Add((v.Name, v.Symbol, v.Category, false, i));
            }
            for (int i = 0; i < ArticulatedVehicles.Count; i++)
            {
                var v = ArticulatedVehicles[i];
                list.Add((v.Name, v.Symbol, v.Category, true, i));
            }
            return list;
        }

        /// <summary>Force reload of all vehicles.</summary>
        public static void Reload()
        {
            _singleUnits = null;
            _articulatedVehicles = null;
            LoadAll();
        }

        private static void LoadAll()
        {
            _singleUnits = new List<VehicleUnit>(GetAashtoSingleUnits());
            _articulatedVehicles = new List<ArticulatedVehicle>(GetAashtoArticulated());

            // Load custom vehicles from disk
            if (Directory.Exists(_customDir))
            {
                foreach (var file in Directory.GetFiles(_customDir, "*.json"))
                {
                    try
                    {
                        string json = File.ReadAllText(file);
                        if (json.Contains("\"trailers\""))
                        {
                            var av = JsonSerializer.Deserialize<ArticulatedVehicle>(json);
                            if (av != null) _articulatedVehicles.Add(av);
                        }
                        else
                        {
                            var vu = JsonSerializer.Deserialize<VehicleUnit>(json);
                            if (vu != null) _singleUnits.Add(vu);
                        }
                    }
                    catch { /* skip corrupt files */ }
                }
            }
        }

        /// <summary>Save a custom vehicle to disk.</summary>
        public static void SaveCustomVehicle(VehicleUnit vehicle)
        {
            Directory.CreateDirectory(_customDir);
            string path = Path.Combine(_customDir, SanitizeFilename(vehicle.Symbol) + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(vehicle, new JsonSerializerOptions { WriteIndented = true }));
            Reload();
        }

        /// <summary>Save a custom articulated vehicle to disk.</summary>
        public static void SaveCustomVehicle(ArticulatedVehicle vehicle)
        {
            Directory.CreateDirectory(_customDir);
            string path = Path.Combine(_customDir, SanitizeFilename(vehicle.Symbol) + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(vehicle, new JsonSerializerOptions { WriteIndented = true }));
            Reload();
        }

        private static string SanitizeFilename(string name)
            => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        // ══════════════════════════════════════════════════════════════
        //  AASHTO Standard Vehicle Definitions
        // ══════════════════════════════════════════════════════════════

        private static VehicleUnit[] GetAashtoSingleUnits() => new[]
        {
            new VehicleUnit
            {
                Name = "Passenger Car", Symbol = "P",
                Category = "Passenger",
                Length = 19.0, Width = 7.0, Wheelbase = 11.0,
                FrontOverhang = 3.0, RearOverhang = 5.0,
                TrackWidth = 6.0, MaxSteeringAngle = ToRad(38.9),
                LockToLockTime = 3.5, MinTurningRadius = 23.8
            },
            new VehicleUnit
            {
                Name = "Single Unit Truck (30')", Symbol = "SU-30",
                Category = "Single Unit",
                Length = 30.0, Width = 8.0, Wheelbase = 20.0,
                FrontOverhang = 4.0, RearOverhang = 6.0,
                TrackWidth = 8.0, MaxSteeringAngle = ToRad(31.5),
                LockToLockTime = 4.5, MinTurningRadius = 41.8
            },
            new VehicleUnit
            {
                Name = "Single Unit Truck 3-Axle (40')", Symbol = "SU-40",
                Category = "Single Unit",
                Length = 39.5, Width = 8.0, Wheelbase = 25.0,
                FrontOverhang = 4.0, RearOverhang = 10.5,
                TrackWidth = 8.0, MaxSteeringAngle = ToRad(29.4),
                LockToLockTime = 5.0, MinTurningRadius = 51.2
            },
            new VehicleUnit
            {
                Name = "City Transit Bus", Symbol = "CITY-BUS",
                Category = "Bus",
                Length = 40.0, Width = 8.5, Wheelbase = 25.0,
                FrontOverhang = 7.0, RearOverhang = 8.0,
                TrackWidth = 8.5, MaxSteeringAngle = ToRad(38.7),
                LockToLockTime = 5.0, MinTurningRadius = 41.6
            },
            new VehicleUnit
            {
                Name = "School Bus (65 passengers)", Symbol = "S-BUS-36",
                Category = "Bus",
                Length = 35.8, Width = 8.0, Wheelbase = 21.3,
                FrontOverhang = 2.5, RearOverhang = 12.0,
                TrackWidth = 7.0, MaxSteeringAngle = ToRad(34.4),
                LockToLockTime = 4.5, MinTurningRadius = 38.6
            },
            new VehicleUnit
            {
                Name = "School Bus (84 passengers)", Symbol = "S-BUS-40",
                Category = "Bus",
                Length = 40.0, Width = 8.0, Wheelbase = 24.5,
                FrontOverhang = 2.5, RearOverhang = 13.0,
                TrackWidth = 7.5, MaxSteeringAngle = ToRad(33.0),
                LockToLockTime = 5.0, MinTurningRadius = 42.0
            },
            new VehicleUnit
            {
                Name = "Articulated Bus (single body)", Symbol = "A-BUS",
                Category = "Bus",
                Length = 60.0, Width = 8.5, Wheelbase = 40.0,
                FrontOverhang = 8.6, RearOverhang = 11.4,
                TrackWidth = 8.5, MaxSteeringAngle = ToRad(28.0),
                LockToLockTime = 5.5, MinTurningRadius = 42.0
            },
            new VehicleUnit
            {
                Name = "Motor Home", Symbol = "MH",
                Category = "Recreational",
                Length = 30.0, Width = 8.0, Wheelbase = 20.0,
                FrontOverhang = 4.0, RearOverhang = 6.0,
                TrackWidth = 7.0, MaxSteeringAngle = ToRad(33.0),
                LockToLockTime = 4.0, MinTurningRadius = 39.7
            },
            // ── Miami-Dade Specific Vehicles ────────────────────────
            new VehicleUnit
            {
                // City of Hialeah Fire Dept. Aerial Tower — spec sheet values:
                //   Length 44.16', WB 22.58', Width 8.42', Track 8.42',
                //   Front overhang 7.83', Rear overhang 13.75' (= L - WB - FOH),
                //   Max wheel (physical inner) angle 42°, Lock-to-lock 4.0 s.
                // MaxSteeringAngle stores the CENTERLINE (bicycle-model) angle used
                // by SweptPathSolver. Convert 42° physical inner → centerline via
                // Ackermann:  R = WB/tan(42°) + T/2 = 25.08 + 4.21 = 29.29',
                //             α_c = atan(WB/R) = atan(22.58/29.29) ≈ 37.63°.
                Name = "City of Hialeah Fire Dept. Aerial Tower", Symbol = "HLH-AERIAL",
                Category = "Fire Apparatus", IsFloridaVehicle = true,
                Length = 44.16, Width = 8.42, Wheelbase = 22.58,
                FrontOverhang = 7.83, RearOverhang = 13.75,
                TrackWidth = 8.42, MaxSteeringAngle = ToRad(37.63),
                LockToLockTime = 4.0, MinTurningRadius = 29.29
            },
            new VehicleUnit
            {
                Name = "Miami-Dade Fire Pumper", Symbol = "MD-PUMP",
                Category = "Fire Apparatus", IsFloridaVehicle = true,
                Length = 35.0, Width = 8.5, Wheelbase = 20.0,
                FrontOverhang = 5.5, RearOverhang = 9.5,
                TrackWidth = 8.0, MaxSteeringAngle = ToRad(35.0),
                LockToLockTime = 4.5, MinTurningRadius = 38.0
            },
            new VehicleUnit
            {
                Name = "Miami-Dade Rear-Load Garbage Truck", Symbol = "MD-GARB",
                Category = "Refuse", IsFloridaVehicle = true,
                Length = 35.0, Width = 8.0, Wheelbase = 22.0,
                FrontOverhang = 5.0, RearOverhang = 8.0,
                TrackWidth = 7.5, MaxSteeringAngle = ToRad(32.0),
                LockToLockTime = 5.0, MinTurningRadius = 42.0
            },
            new VehicleUnit
            {
                Name = "Miami-Dade Front-Load Garbage Truck", Symbol = "MD-GARBFL",
                Category = "Refuse", IsFloridaVehicle = true,
                Length = 33.0, Width = 8.0, Wheelbase = 20.0,
                FrontOverhang = 8.0, RearOverhang = 5.0,
                TrackWidth = 7.5, MaxSteeringAngle = ToRad(33.0),
                LockToLockTime = 5.0, MinTurningRadius = 40.0
            }
        };

        private static ArticulatedVehicle[] GetAashtoArticulated() => new[]
        {
            // WB-40: Intermediate Semitrailer
            new ArticulatedVehicle
            {
                Name = "Intermediate Semitrailer", Symbol = "WB-40",
                Category = "Semi",
                TotalLength = 45.5,
                LeadUnit = new VehicleUnit
                {
                    Name = "WB-40 Tractor", Symbol = "WB-40-T",
                    Length = 15.5, Width = 8.0, Wheelbase = 12.5,
                    FrontOverhang = 3.0, RearOverhang = 0.0,
                    TrackWidth = 8.0, MaxSteeringAngle = ToRad(36.0),
                    LockToLockTime = 4.5, MinTurningRadius = 39.9
                },
                Trailers = new[]
                {
                    new TrailerDef
                    {
                        Unit = new VehicleUnit
                        {
                            Name = "WB-40 Trailer", Symbol = "WB-40-TR",
                            Length = 33.0, Width = 8.0, Wheelbase = 27.5,
                            FrontOverhang = 3.0, RearOverhang = 2.5,
                            TrackWidth = 8.0
                        },
                        Coupling = new CouplingDef
                        {
                            Type = CouplingType.FifthWheel,
                            HitchOffset = 0.0,
                            KingpinOffset = 3.0
                        }
                    }
                }
            },

            // WB-62: Interstate Semitrailer
            new ArticulatedVehicle
            {
                Name = "Interstate Semitrailer", Symbol = "WB-62",
                Category = "Semi",
                TotalLength = 68.5,
                LeadUnit = new VehicleUnit
                {
                    Name = "WB-62 Tractor", Symbol = "WB-62-T",
                    Length = 21.0, Width = 8.5, Wheelbase = 14.5,
                    FrontOverhang = 3.0, RearOverhang = 3.5,
                    TrackWidth = 8.0, MaxSteeringAngle = ToRad(28.4),
                    LockToLockTime = 5.0, MinTurningRadius = 44.8
                },
                Trailers = new[]
                {
                    new TrailerDef
                    {
                        Unit = new VehicleUnit
                        {
                            Name = "WB-62 Trailer", Symbol = "WB-62-TR",
                            Length = 53.0, Width = 8.5, Wheelbase = 40.5,
                            FrontOverhang = 4.0, RearOverhang = 8.5,
                            TrackWidth = 8.0
                        },
                        Coupling = new CouplingDef
                        {
                            Type = CouplingType.FifthWheel,
                            HitchOffset = 3.5,
                            KingpinOffset = 4.0
                        }
                    }
                }
            },

            // WB-62FL: Florida Interstate Semi
            new ArticulatedVehicle
            {
                Name = "Florida Interstate Semitrailer", Symbol = "WB-62FL",
                Category = "Semi", IsFloridaVehicle = true,
                TotalLength = 69.0,
                LeadUnit = new VehicleUnit
                {
                    Name = "WB-62FL Tractor", Symbol = "WB-62FL-T",
                    IsFloridaVehicle = true,
                    Length = 21.5, Width = 8.5, Wheelbase = 14.5,
                    FrontOverhang = 3.0, RearOverhang = 4.0,
                    TrackWidth = 8.0, MaxSteeringAngle = ToRad(28.4),
                    LockToLockTime = 5.0, MinTurningRadius = 45.0
                },
                Trailers = new[]
                {
                    new TrailerDef
                    {
                        Unit = new VehicleUnit
                        {
                            Name = "WB-62FL Trailer", Symbol = "WB-62FL-TR",
                            IsFloridaVehicle = true,
                            Length = 53.0, Width = 8.5, Wheelbase = 41.0,
                            FrontOverhang = 3.5, RearOverhang = 8.5,
                            TrackWidth = 8.0
                        },
                        Coupling = new CouplingDef
                        {
                            Type = CouplingType.FifthWheel,
                            HitchOffset = 4.0,
                            KingpinOffset = 3.5
                        }
                    }
                }
            },

            // WB-109D: Tandem Double
            new ArticulatedVehicle
            {
                Name = "Tandem Double Semitrailer", Symbol = "WB-109D",
                Category = "Double",
                TotalLength = 114.0,
                LeadUnit = new VehicleUnit
                {
                    Name = "WB-109D Tractor", Symbol = "WB-109D-T",
                    Length = 21.0, Width = 8.5, Wheelbase = 14.5,
                    FrontOverhang = 3.0, RearOverhang = 3.5,
                    TrackWidth = 8.0, MaxSteeringAngle = ToRad(22.5),
                    LockToLockTime = 5.5, MinTurningRadius = 59.9
                },
                Trailers = new[]
                {
                    new TrailerDef
                    {
                        Unit = new VehicleUnit
                        {
                            Name = "WB-109D Lead Trailer", Symbol = "WB-109D-T1",
                            Length = 48.0, Width = 8.5, Wheelbase = 40.0,
                            FrontOverhang = 4.0, RearOverhang = 4.0,
                            TrackWidth = 8.0
                        },
                        Coupling = new CouplingDef
                        {
                            Type = CouplingType.FifthWheel,
                            HitchOffset = 3.5,
                            KingpinOffset = 4.0
                        }
                    },
                    new TrailerDef
                    {
                        Unit = new VehicleUnit
                        {
                            Name = "WB-109D Rear Trailer", Symbol = "WB-109D-T2",
                            Length = 28.5, Width = 8.5, Wheelbase = 20.0,
                            FrontOverhang = 4.0, RearOverhang = 4.5,
                            TrackWidth = 8.0
                        },
                        Coupling = new CouplingDef
                        {
                            Type = CouplingType.Drawbar,
                            HitchOffset = 4.0,
                            KingpinOffset = 4.0
                        }
                    }
                }
            }
        };

        private static double ToRad(double deg) => deg * Math.PI / 180.0;
    }

    /// <summary>
    /// Florida-specific parking dimension defaults (South Florida typical).
    /// </summary>
    public static class FloridaParkingDefaults
    {
        public static ParkingDimensions Get90Degree() => new()
        {
            Angle = ParkingAngle.Perpendicular,
            StallWidth = 9.0, StallDepth = 18.5,
            AisleWidthOneWay = 24.0, AisleWidthTwoWay = 24.0
        };

        public static ParkingDimensions Get60Degree() => new()
        {
            Angle = ParkingAngle.Angle60,
            StallWidth = 9.0, StallDepth = 20.0,
            AisleWidthOneWay = 16.0, AisleWidthTwoWay = 24.0
        };

        public static ParkingDimensions Get45Degree() => new()
        {
            Angle = ParkingAngle.Angle45,
            StallWidth = 9.0, StallDepth = 19.0,
            AisleWidthOneWay = 13.0, AisleWidthTwoWay = 24.0
        };

        public static ParkingDimensions Get30Degree() => new()
        {
            Angle = ParkingAngle.Angle30,
            StallWidth = 9.0, StallDepth = 17.0,
            AisleWidthOneWay = 12.0, AisleWidthTwoWay = 24.0
        };

        public static ParkingDimensions GetParallel() => new()
        {
            Angle = ParkingAngle.Parallel,
            StallWidth = 9.0, StallDepth = 22.0,
            AisleWidthOneWay = 12.0, AisleWidthTwoWay = 24.0
        };

        public static ParkingDimensions GetByAngle(ParkingAngle angle) => angle switch
        {
            ParkingAngle.Perpendicular => Get90Degree(),
            ParkingAngle.Angle60 => Get60Degree(),
            ParkingAngle.Angle45 => Get45Degree(),
            ParkingAngle.Angle30 => Get30Degree(),
            ParkingAngle.Parallel => GetParallel(),
            _ => Get90Degree()
        };

        /// <summary>Florida ADA requirements per F.S. §553.5041.</summary>
        public static AdaRequirements GetAdaRequirements() => new()
        {
            StandardWidth = 8.0,
            StandardAisle = 5.0,
            VanWidth = 11.0,
            VanAisle = 5.0,
            VanClearanceInches = 98.0,
            MaxSlope = 0.02
        };

        /// <summary>
        /// Required number of accessible spaces based on total count.
        /// Per Florida Building Code / ADA Standards.
        /// </summary>
        public static int RequiredAccessibleSpaces(int totalSpaces)
        {
            if (totalSpaces <= 25) return 1;
            if (totalSpaces <= 50) return 2;
            if (totalSpaces <= 75) return 3;
            if (totalSpaces <= 100) return 4;
            if (totalSpaces <= 150) return 5;
            if (totalSpaces <= 200) return 6;
            if (totalSpaces <= 300) return 7;
            if (totalSpaces <= 400) return 8;
            if (totalSpaces <= 500) return 9;
            if (totalSpaces <= 1000) return (totalSpaces / 50) + 1; // 2% of total
            return 20 + (totalSpaces - 1000) / 100;
        }
    }
}
