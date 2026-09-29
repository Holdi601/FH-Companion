namespace ForzaHaptics.Tester;

internal static class SelfTest
{
    public static void Run()
    {
        var packet = new byte[324];
        BitConverter.GetBytes(1).CopyTo(packet, 0);
        BitConverter.GetBytes(8000f).CopyTo(packet, 8);
        BitConverter.GetBytes(4000f).CopyTo(packet, 16);
        BitConverter.GetBytes(0.50f).CopyTo(packet, 180);
        BitConverter.GetBytes(0.05f).CopyTo(packet, 184);
        BitConverter.GetBytes(0.50f).CopyTo(packet, 188);
        BitConverter.GetBytes(0.05f).CopyTo(packet, 192);
        BitConverter.GetBytes(30f).CopyTo(packet, 256);
        packet[316] = 200;

        if (!ForzaPacket.TryParse(packet, out var telemetry))
        {
            throw new InvalidOperationException("The official 324-byte FH6 packet did not parse.");
        }

        AssertNear(telemetry.Get("Speed"), 30, "Speed offset");
        AssertNear(telemetry.Brake, 200 / 255.0, "Brake offset");
        AssertNear(telemetry.LeftGrip, 0.5, "Derived left grip");
        AssertNear(telemetry.Get("Derived.SpeedKmh"), 108, "Derived km/h");

        CheckGameWatch();
        CheckPanelScrolling();
        CheckLapDelta();
        CheckPositionDelta();
        CheckSprintRecording();
        CheckLiveMapResets();
        CheckOwnCars();
        CheckCourseIdentity();
        CheckRouteLengths();
        CheckRouteTable();
        CheckCarNotes();
        CheckTelemetryTrack();
        CheckDesktopShortcut();
        CheckServerFallback();
        CheckOverlaysReachTheScreen();
        CheckFreeRoamLines();
        CheckFreeRoamTimeAttack();
        CheckTuningInspector();
        CheckMemoryScanner();
        CheckPartNames();
        EdgeCaseTest.Run();

        // DER STANDARDGRAPH: still bei maessigem Schlupf, kraeftig am Limit, stumm ohne
        // Pakete. Das Paket oben hat links Grip 0,5 und rechts 0,95.
        var graph = SignalGraph.CreateDefault();
        GraphHapticOutput Kanal(GraphEvaluationResult r, int kanal) => r.Outputs.Single(output => output.Channel == kanal);
        var result = new SignalGraphEvaluator().Evaluate(graph, telemetry, DateTime.UtcNow);
        // "Still" heisst unter 1 %: die Kurve laeuft flach aus 0 heraus und liefert bei
        // 5 % Griffverlust noch 0,0002 -- das ist am Controller nicht zu spueren.
        if (Kanal(result, SteamControllerHaptics.LeftGrip).Strength >= 0.1
            || Kanal(result, SteamControllerHaptics.RightGrip).Strength > 0.01)
        {
            throw new InvalidOperationException(
                "Default graph: moderate slip is not quiet -- left="
                + $"{Kanal(result, SteamControllerHaptics.LeftGrip).Strength:F3}, right={Kanal(result, SteamControllerHaptics.RightGrip).Strength:F3}.");
        }
        var amLimit = (byte[])packet.Clone();
        BitConverter.GetBytes(0.95f).CopyTo(amLimit, 180);
        if (!ForzaPacket.TryParse(amLimit, out var limitTelemetry))
        {
            throw new InvalidOperationException("Default graph: the test packet at the limit did not parse.");
        }
        var limit = new SignalGraphEvaluator().Evaluate(graph, limitTelemetry, DateTime.UtcNow);
        if (Kanal(limit, SteamControllerHaptics.LeftGrip).Strength <= 0.4)
        {
            throw new InvalidOperationException(
                $"Default graph: grip at the limit barely vibrates ({Kanal(limit, SteamControllerHaptics.LeftGrip).Strength:F3}).");
        }
        var ohnePakete = new SignalGraphEvaluator().Evaluate(graph, null, DateTime.UtcNow);
        if (ohnePakete.Outputs.Any(output => output.Strength > 0.0001))
        {
            throw new InvalidOperationException(
                "Default graph: without telemetry an output still vibrates -- zero must mean silence.");
        }
        var steam = new ControllerOutputTarget(OutputSignalNode.SteamNativeTargetId, "Steam", true, false, true, false);
        if (graph.Nodes.OfType<OutputSignalNode>().Any(output => !BlueprintEditor.KanalGibtEs(steam, output.Channel)))
        {
            throw new InvalidOperationException("Default graph: an output uses a channel the Steam Controller does not have.");
        }

        var constantGraph = new SignalGraph();
        var constant = new ConstantSignalNode("50% test", new Point(10, 10), 0.5);
        var constantOutput = new OutputSignalNode(
            "Test output",
            new Point(260, 10),
            SteamControllerHaptics.LeftPad,
            HapticEffectMode.Rumble,
            220);
        constantGraph.Nodes.AddRange([constant, constantOutput]);
        constantGraph.Connect(constant.Id, constantOutput.Id);
        var constantResult = new SignalGraphEvaluator().Evaluate(
            constantGraph,
            null,
            DateTime.UtcNow);
        AssertNear(
            constantResult.Outputs.Single().Strength,
            0.5,
            "Constant test node without telemetry");
        AssertNear(
            constantResult.Outputs.Single().FrequencyHz,
            220,
            "Fixed output frequency");

        var secondConstant = new ConstantSignalNode("90% test", new Point(10, 130), 0.9);
        constantGraph.Nodes.Add(secondConstant);
        constantGraph.Connect(secondConstant.Id, constantOutput.Id);
        var maximumResult = new SignalGraphEvaluator().Evaluate(
            constantGraph,
            null,
            DateTime.UtcNow);
        AssertNear(maximumResult.Outputs.Single().Strength, 0.9, "Maximum input combination");

        constantOutput.InputMode = SignalGroupMode.Average;
        constantOutput.ModulationMode = HapticModulationMode.FrequencyOnly;
        constantOutput.TargetId = GenericGamepadHaptics.AllTargetId;
        constantOutput.Channel = GenericGamepadHaptics.FrequencyMix;
        constantOutput.MaximumStrength = 0.4;
        constantOutput.MinimumFrequencyHz = 100;
        constantOutput.MaximumFrequencyHz = 300;
        var frequencyResult = new SignalGraphEvaluator().Evaluate(
            constantGraph,
            null,
            DateTime.UtcNow);
        AssertNear(frequencyResult.NodeValues[constantOutput.Id], 0.7, "Average input combination");
        AssertNear(frequencyResult.Outputs.Single().Strength, 0.4, "Fixed modulation strength");
        AssertNear(frequencyResult.Outputs.Single().FrequencyHz, 240, "Frequency modulation");
        if (frequencyResult.Outputs.Single().TargetId != GenericGamepadHaptics.AllTargetId)
        {
            throw new InvalidOperationException("Controller target was not propagated to graph output.");
        }

        using (var editor = new BlueprintEditor(graph, ForzaPacket.AllDescriptors))
        {
            var outputNode = graph.Nodes.OfType<OutputSignalNode>().First();
            editor.SelectNodeForTest(outputNode);
            // 20 Hz: der Standardgraph rumpelt seit 2026-09-26 so tief wie das Profil
            // des Nutzers (vorher 70 Hz).
            AssertNear(outputNode.MinimumFrequencyHz, 20, "Output property editor frequency");
            var propertyValues = editor.GetPropertyNumberValuesForTest();
            if (!propertyValues.Contains(20))
            {
                throw new InvalidOperationException("Output frequency control was not created.");
            }
        }

        var genericOutput = new OutputSignalNode(
            "Generic test",
            new Point(20, 20),
            GenericGamepadHaptics.FrequencyMix,
            HapticEffectMode.Rumble,
            100)
        {
            TargetId = "generic:1234:5678:0"
        };
        using (var genericEditor = new BlueprintEditor(
                   new SignalGraph(),
                   ForzaPacket.AllDescriptors,
                   () =>
                   [
                       new ControllerOutputTarget(
                           genericOutput.TargetId,
                           "Test Xbox-compatible pad",
                           false,
                           false,
                           true,
                           true)
                   ],
                   () => genericOutput.TargetId))
        {
            genericEditor.SelectNodeForTest(genericOutput);
            var items = genericEditor.GetPropertyComboItemsForTest();
            if (!items.Any(item => item.Contains("Frequency mix", StringComparison.Ordinal)) ||
                !items.Any(item => item.Contains("trigger motor", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("Generic gamepad output choices were not created.");
            }

            var inheritedOutput = genericEditor.CreateOutputForSelectedControllerForTest();
            if (inheritedOutput.TargetId != genericOutput.TargetId ||
                inheritedOutput.Channel != GenericGamepadHaptics.FrequencyMix)
            {
                throw new InvalidOperationException(
                    "New output node did not inherit the active controller.");
            }
        }

        var lowMix = GenericGamepadHaptics.MapOutputForTest(
            GenericGamepadHaptics.FrequencyMix,
            1,
            20,
            false);
        var highMix = GenericGamepadHaptics.MapOutputForTest(
            GenericGamepadHaptics.FrequencyMix,
            1,
            800,
            false);
        AssertNear(lowMix.Low, 1, "Generic low-frequency mix");
        AssertNear(lowMix.High, 0, "Generic low-frequency high motor");
        AssertNear(highMix.Low, 0, "Generic high-frequency low motor");
        AssertNear(highMix.High, 1, "Generic high-frequency mix");

        var triggerOutput = frequencyResult.Outputs.Single() with
        {
            TargetId = "dualsense:0CE6:0",
            Channel = DualSenseHaptics.RightAdaptiveTrigger,
            Strength = 0.75,
            FrequencyHz = 42,
            DualSenseTriggerEffect = DualSenseTriggerEffectMode.WeaponClick,
            TriggerStartPosition = 0.3,
            TriggerEndPosition = 0.7
        };
        var weaponEffect = DualSenseHaptics.BuildTriggerEffectForTest(triggerOutput);
        if (weaponEffect.Length != 11 || weaponEffect[0] != 0x25)
        {
            throw new InvalidOperationException("DualSense weapon/click effect packing failed.");
        }

        var resistanceEffect = DualSenseHaptics.BuildTriggerEffectForTest(
            triggerOutput with
            {
                DualSenseTriggerEffect = DualSenseTriggerEffectMode.Resistance
            });
        var slopeEffect = DualSenseHaptics.BuildTriggerEffectForTest(
            triggerOutput with
            {
                DualSenseTriggerEffect = DualSenseTriggerEffectMode.TensionSlope
            });
        var vibrationEffect = DualSenseHaptics.BuildTriggerEffectForTest(
            triggerOutput with
            {
                DualSenseTriggerEffect = DualSenseTriggerEffectMode.Vibration
            });
        var bowEffect = DualSenseHaptics.BuildTriggerEffectForTest(
            triggerOutput with
            {
                DualSenseTriggerEffect = DualSenseTriggerEffectMode.BowSnap
            });
        if (resistanceEffect[0] != 0x21 ||
            slopeEffect[0] != 0x21 ||
            vibrationEffect[0] != 0x26 ||
            vibrationEffect[9] != 42 ||
            bowEffect[0] != 0x22)
        {
            throw new InvalidOperationException("DualSense adaptive-trigger effect packing failed.");
        }

        var usbReport = DualSenseHaptics.BuildReportForTest(
            false,
            0.25,
            0.5,
            null,
            triggerOutput);
        if (usbReport.Length != 48 ||
            usbReport[0] != 0x02 ||
            usbReport[1] != 0x0F ||
            usbReport[11] != 0x25)
        {
            throw new InvalidOperationException("DualSense USB output report packing failed.");
        }

        var bluetoothReport = DualSenseHaptics.BuildReportForTest(
            true,
            0.25,
            0.5,
            null,
            triggerOutput);
        if (bluetoothReport.Length != 78 ||
            bluetoothReport[0] != 0x31 ||
            bluetoothReport[2] != 0x10 ||
            bluetoothReport[13] != 0x25 ||
            BitConverter.ToUInt32(bluetoothReport, 74) !=
            ComputeDualSenseBluetoothCrc(bluetoothReport))
        {
            throw new InvalidOperationException("DualSense Bluetooth output report packing failed.");
        }

        var dualSenseOutput = new OutputSignalNode(
            "DualSense R2",
            new Point(20, 20),
            DualSenseHaptics.RightAdaptiveTrigger,
            HapticEffectMode.Rumble,
            40)
        {
            TargetId = "dualsense:0CE6:0",
            DualSenseTriggerEffect = DualSenseTriggerEffectMode.WeaponClick
        };
        using (var dualSenseEditor = new BlueprintEditor(
                   new SignalGraph(),
                   ForzaPacket.AllDescriptors,
                   () =>
                   [
                       new ControllerOutputTarget(
                           dualSenseOutput.TargetId,
                           "DualSense native test",
                           false,
                           true,
                           true,
                           false)
                   ],
                   () => dualSenseOutput.TargetId))
        {
            dualSenseEditor.SelectNodeForTest(dualSenseOutput);
            var items = dualSenseEditor.GetPropertyComboItemsForTest();
            if (!items.Any(item => item.Contains("L2 adaptive", StringComparison.Ordinal)) ||
                !items.Any(item => item.Contains("R2 adaptive", StringComparison.Ordinal)) ||
                !items.Any(item => item.Contains("WeaponClick", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    "DualSense adaptive-trigger output choices were not created.");
            }
        }

        var profilePath = Path.Combine(Path.GetTempPath(), $"forza-haptics-{Guid.NewGuid():N}.json");
        try
        {
            var savedOutput = graph.Nodes.OfType<OutputSignalNode>().First();
            savedOutput.InputMode = SignalGroupMode.SumClamped;
            savedOutput.ModulationMode = HapticModulationMode.StrengthAndFrequency;
            savedOutput.TargetId = GenericGamepadHaptics.AllTargetId;
            savedOutput.Channel = GenericGamepadHaptics.FrequencyMix;
            savedOutput.MinimumStrength = 0.15;
            savedOutput.MaximumStrength = 0.85;
            savedOutput.MinimumFrequencyHz = 80;
            savedOutput.MaximumFrequencyHz = 360;
            savedOutput.BeepDutyCycle = 0.35;
            savedOutput.DualSenseTriggerEffect = DualSenseTriggerEffectMode.BowSnap;
            savedOutput.TriggerStartPosition = 0.3;
            savedOutput.TriggerEndPosition = 0.85;
            savedOutput.TriggerSecondaryStrength = 0.4;
            savedOutput.TriggerSnapStrength = 0.65;
            SignalGraphPersistence.Save(graph, profilePath);
            var loaded = new SignalGraph();
            SignalGraphPersistence.LoadInto(loaded, profilePath);
            if (loaded.Nodes.Count != graph.Nodes.Count ||
                loaded.Connections.Count != graph.Connections.Count)
            {
                throw new InvalidOperationException("Graph profile round-trip failed.");
            }

            var loadedOutput = loaded.Nodes.OfType<OutputSignalNode>().First();
            if (loadedOutput.InputMode != SignalGroupMode.SumClamped ||
                loadedOutput.ModulationMode != HapticModulationMode.StrengthAndFrequency ||
                loadedOutput.TargetId != GenericGamepadHaptics.AllTargetId ||
                loadedOutput.Channel != GenericGamepadHaptics.FrequencyMix)
            {
                throw new InvalidOperationException("Output modulation modes were not persisted.");
            }

            AssertNear(loadedOutput.MinimumStrength, 0.15, "Persisted minimum strength");
            AssertNear(loadedOutput.MaximumStrength, 0.85, "Persisted maximum strength");
            AssertNear(loadedOutput.MinimumFrequencyHz, 80, "Persisted minimum frequency");
            AssertNear(loadedOutput.MaximumFrequencyHz, 360, "Persisted maximum frequency");
            AssertNear(loadedOutput.BeepDutyCycle, 0.35, "Persisted pulse duty cycle");
            if (loadedOutput.DualSenseTriggerEffect != DualSenseTriggerEffectMode.BowSnap)
            {
                throw new InvalidOperationException(
                    "DualSense trigger effect mode was not persisted.");
            }

            AssertNear(loadedOutput.TriggerStartPosition, 0.3, "Persisted trigger start");
            AssertNear(loadedOutput.TriggerEndPosition, 0.85, "Persisted trigger end");
            AssertNear(
                loadedOutput.TriggerSecondaryStrength,
                0.4,
                "Persisted trigger secondary strength");
            AssertNear(
                loadedOutput.TriggerSnapStrength,
                0.65,
                "Persisted trigger snap strength");
        }
        finally
        {
            if (File.Exists(profilePath))
            {
                File.Delete(profilePath);
            }
        }
    }

    /// <summary>Die Anwesenheitspruefung, an der jetzt zwei Sperren haengen.</summary>
    /// <remarks>
    /// Sie entscheidet, ob die Haptik den Controller anfassen und ob das Overlay
    /// erscheinen darf. Faellt sie auf "nie da", ist die App still und niemand
    /// erfaehrt warum; faellt sie auf "immer da", sind beide Sperren wirkungslos.
    /// Beide Richtungen werden also geprueft, und der eigene Prozess ist der einzige,
    /// von dem hier sicher bekannt ist, dass er laeuft.
    /// </remarks>
    /// <summary>Ein Paket mit genau den Feldern, die die Rundenaufnahme liest.</summary>
    /// <summary>
    /// Ein nachgestelltes Paket. <paramref name="distance"/> ist der Weg vom
    /// Ursprung -- das Auto steht wirklich dort.
    /// </summary>
    /// <remarks>
    /// Die Position wird MITGESETZT, nicht nur das Feld `DistanceTraveled`. Seit dem
    /// 2026-09-12 rechnet die Aufzeichnung die gefahrene Strecke aus den
    /// Weltkoordinaten, weil sich `DistanceTraveled` als unbrauchbar erwiesen hat
    /// (jede Runde ~5.950 m, unabhaengig von ihrer Dauer). Ein Test, der das Auto
    /// nur im alten Feld bewegt, prueft damit nichts mehr -- und genau so ist er
    /// beim ersten Lauf nach der Aenderung durchgefallen.
    /// </remarks>
    internal static byte[] LapPacket(int raceOn, int lapNumber, float distance,
                                    float lapTime, float lastLap, int ordinal,
                                    int pi, int carClass, float maxRpm = 7000f,
                                    int cylinders = 6, int drivetrain = 1,
                                    float? x = null, float? z = null,
                                    uint timestampMs = 0, float speed = 0f)
    {
        var packet = new byte[324];
        BitConverter.GetBytes(timestampMs).CopyTo(packet, 4);
        BitConverter.GetBytes(speed).CopyTo(packet, 256);
        // Ohne Angabe auf einer Geraden entlang X: der Weg ist dann die Strecke.
        BitConverter.GetBytes(x ?? distance).CopyTo(packet, 244);
        BitConverter.GetBytes(0f).CopyTo(packet, 248);
        BitConverter.GetBytes(z ?? 0f).CopyTo(packet, 252);
        BitConverter.GetBytes(raceOn).CopyTo(packet, 0);
        BitConverter.GetBytes(maxRpm).CopyTo(packet, 8);
        BitConverter.GetBytes(900f).CopyTo(packet, 12);
        BitConverter.GetBytes(drivetrain).CopyTo(packet, 224);
        BitConverter.GetBytes(cylinders).CopyTo(packet, 228);
        BitConverter.GetBytes(ordinal).CopyTo(packet, 212);
        BitConverter.GetBytes(carClass).CopyTo(packet, 216);
        BitConverter.GetBytes(pi).CopyTo(packet, 220);
        BitConverter.GetBytes(distance).CopyTo(packet, 292);
        BitConverter.GetBytes(lastLap).CopyTo(packet, 300);
        BitConverter.GetBytes(lapTime).CopyTo(packet, 304);
        BitConverter.GetBytes((ushort)lapNumber).CopyTo(packet, 312);
        return packet;
    }

    /// <summary>Rundenaufnahme und Delta, an einem nachgestellten Rennen.</summary>
    /// <remarks>
    /// Forza sendet KEINE Checkpoints. Das Delta entsteht darum aus der gefahrenen
    /// Strecke: "wie lange brauchte die Bestzeit bis genau hierher". Was hier geprueft
    /// wird, ist die Kette, die das traegt -- Rundengrenze erkennen, Messpunkte
    /// setzen, zwischen ihnen interpolieren, und die Referenz nur so weit fassen wie
    /// gefragt.
    ///
    /// Zwei Runden werden gefahren: eine langsame und eine um 10 % schnellere. Das
    /// Delta bei halber Strecke muss dann rund ein Zehntel der bisherigen Zeit
    /// betragen, mit umgekehrtem Vorzeichen.
    /// </remarks>
    private static void CheckLapDelta()
    {
        const float laenge = 2000f;
        var aufnahme = new Rivals.LapRecorder();
        var fertige = new List<Rivals.RecordedLap>();
        aufnahme.LapCompleted += (_, lap) => fertige.Add(lap);

        // IM KREIS, nicht geradeaus.
        //
        // Eine gerade Teststrecke laesst jede Runde 1.000 m entfernt von der
        // vorigen beginnen -- und damit gelten sie zu Recht als verschiedene
        // Strassen, seit die Zuordnung ueber den Ort laeuft. Ein Rundkurs kehrt zum
        // Startpunkt zurueck; genau das macht ihn zum Rundkurs, und genau das muss
        // die Testfahrt auch tun.
        var radius = laenge / (2f * MathF.PI);
        void Fahre(int runde, float sekundenJeMeter, float startDistanz)
        {
            // In Schritten von 2 m -- klein genug, dass kein Schritt als Sprung
            // (>50 m) verworfen wird, und fein genug fuer die 5-m-Messpunkte.
            for (var m = 0f; m <= laenge; m += 2f)
            {
                var winkel = m / radius;
                if (!ForzaPacket.TryParse(LapPacket(
                        1, runde, startDistanz + m, m * sekundenJeMeter, 0f,
                        1234, 700, 5,
                        x: radius * MathF.Cos(winkel), z: radius * MathF.Sin(winkel)),
                        out var paket))
                {
                    throw new InvalidOperationException("Das nachgestellte Paket parste nicht.");
                }
                aufnahme.OnTelemetry(paket);
            }
        }

        // Runde 1 langsam, Runde 2 zehn Prozent schneller. Der Wechsel auf Runde 3
        // schliesst die zweite ab.
        Fahre(1, 0.040f, 0f);
        Fahre(2, 0.036f, laenge);
        if (!ForzaPacket.TryParse(LapPacket(
                1, 3, laenge * 2, 0f, laenge * 0.036f, 1234, 700, 5,
                x: laenge / (2f * MathF.PI), z: 0f), out var abschluss))
        {
            throw new InvalidOperationException("Das Abschlusspaket parste nicht.");
        }
        aufnahme.OnTelemetry(abschluss);

        if (fertige.Count != 2)
        {
            throw new InvalidOperationException(
                $"Zwei gefahrene Runden ergaben {fertige.Count} aufgezeichnete.");
        }

        var langsam = fertige[0];
        var schnell = fertige[1];
        // 5 cm Luft: der gefahrene Weg wird aus Sehnen aufsummiert.
        AssertNear(langsam.LengthMetres, laenge, "Rundenlaenge", 0.05);
        if (langsam.LengthKey != schnell.LengthKey)
        {
            throw new InvalidOperationException(
                "Zwei Runden derselben Strecke bekamen verschiedene Fingerabdruecke: "
                + $"{langsam.LengthKey} gegen {schnell.LengthKey}.");
        }

        // Zeit bei halber Strecke, zwischen zwei Messpunkten interpoliert.
        var beiHalb = langsam.SecondsAt(laenge / 2);
        if (beiHalb is null) { throw new InvalidOperationException("Keine Zeit bei halber Strecke."); }
        AssertNear(beiHalb.Value, 1000f * 0.040f, "interpolierte Zwischenzeit");

        // Jenseits des Endes gibt es bewusst keine Auskunft statt einer erfundenen.
        if (langsam.SecondsAt(laenge * 2) is not null)
        {
            throw new InvalidOperationException(
                "Hinter dem Rundenende wurde eine Zeit erfunden statt nichts geliefert.");
        }

        // Der Bestand behaelt die schnellere und meldet sie als Referenz.
        var ablage = Path.Combine(Path.GetTempPath(),
            "forza-lapdelta-selftest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var bestand = new Rivals.LapLibrary(ablage);
            bestand.Add(langsam);
            bestand.Add(schnell);
            // BEIDE bleiben: erst aus mehreren Fahrten ergibt sich, wo die Strecke
            // endet. Behalten heisst nicht Massstab sein -- der Massstab ist die
            // schnellste Fahrt BIS ZUM ZIEL, und das ist hier die zweite.
            if (bestand.Laps.Count != 2)
            {
                throw new InvalidOperationException(
                    $"Zwei Runden derselben Strecke ergaben {bestand.Laps.Count} "
                    + "Eintraege statt zweien.");
            }
            var massstabRund = Rivals.LapLibrary.SchnellsteZumZiel(bestand.Laps);
            AssertNear(massstabRund!.LapSeconds, laenge * 0.036f, "Massstab", 0.05);

            var referenz = bestand.Reference(Rivals.DeltaReference.SameCarSameTune,
                                             schnell.LengthMetres, schnell.TuneKey, 1234, 5,
                                             0f, 0f, schnell.StandingStart);
            // Mehrere Eintraege duerfen die Auswahl nicht verwirren.
            if (referenz is null) { throw new InvalidOperationException("Keine Referenz gefunden."); }

            // Eine ENGERE Auswahl ohne eigene Runde darf NICHTS liefern, nicht heimlich
            // die weiter gefasste Bestzeit. Hier: dasselbe Auto, aber umgebaut --
            // andere Drehzahlgrenze, anderer Fingerabdruck.
            var umgebaut = schnell.TuneKey.Replace("/700/", "/780/");
            var fremd = bestand.Reference(Rivals.DeltaReference.SameCarSameTune,
                                          schnell.LengthMetres, umgebaut, 1234, 5,
                                          0f, 0f, schnell.StandingStart);
            if (fremd is not null)
            {
                throw new InvalidOperationException(
                    "Fuer eine Abstimmung ohne eigene Runde kam eine fremde Bestzeit zurueck.");
            }

            // Dasselbe Auto ohne Umbau findet seine Runde weiterhin.
            if (bestand.Reference(Rivals.DeltaReference.SameCar,
                                  schnell.LengthMetres, umgebaut, 1234, 5,
                                          0f, 0f, schnell.StandingStart) is null)
            {
                throw new InvalidOperationException(
                    "'dieses Auto' fand die eigene Runde nicht mehr.");
            }

            // Und der Fingerabdruck muss auf einen Umbau ansprechen: andere
            // Drehzahlgrenze heisst andere Abstimmung.
            var serie = new Rivals.RecordedLap
            {
                CarOrdinal = 1234, PerformanceIndex = 700, Drivetrain = 1,
                Cylinders = 6, MaxRpm = 7000, IdleRpm = 900,
            };
            var getunt = new Rivals.RecordedLap
            {
                CarOrdinal = 1234, PerformanceIndex = 700, Drivetrain = 1,
                Cylinders = 6, MaxRpm = 8200, IdleRpm = 900,
            };
            if (serie.TuneKey == getunt.TuneKey)
            {
                throw new InvalidOperationException(
                    "Eine hoehere Drehzahlgrenze ergab denselben Abstimmungs-Fingerabdruck.");
            }

            // DER FALL AUS DEM ECHTEN RENNEN (2026-09-12): zwei Runden desselben
            // Kurses ergaben 6069 m und 5949 m. Wer daraus zwei Strecken macht,
            // vergleicht nie wieder etwas -- der Streifen stuende auf ewig auf
            // "--.---", und zwar ohne jede Fehlermeldung.
            if (!Rivals.RecordedLap.SameCourse(6069f, 5949f))
            {
                throw new InvalidOperationException(
                    "6069 m und 5949 m -- zwei gemessene Runden desselben Kurses -- "
                    + "gelten als verschiedene Strecken. Dann findet das Delta nie "
                    + "eine Referenz.");
            }

            // STEHEND GEGEN FLIEGEND: nie. Eine Runde von der Aufstellung weg
            // dauert Sekunden laenger als dieselbe Runde fliegend -- der Nutzer sah
            // am 2026-09-12 auf dem Spielschirm, dass er schneller war, waehrend
            // mein Streifen "langsamer" zeigte. Genau daher kam es.
            if (bestand.Reference(Rivals.DeltaReference.SameCar,
                                  schnell.LengthMetres, schnell.TuneKey, 1234, 5,
                                  0f, 0f, !schnell.StandingStart) is not null)
            {
                throw new InvalidOperationException(
                    "Fuer eine Runde mit anderer Startart kam eine Referenz zurueck: "
                    + "stehend und fliegend duerfen nie gegeneinander gehalten werden.");
            }

            // Das zweite gemessene Paar desselben Abends: 6271 m und 5935 m, 5,3 %
            // auseinander, Rundenzeiten 53,6 s und 53,2 s -- unmoeglich zwei
            // verschiedene Kurse.
            if (!Rivals.RecordedLap.SameCourse(6270.7f, 5935.3f))
            {
                throw new InvalidOperationException(
                    "6271 m und 5935 m -- zwei gemessene Runden desselben Kurses -- "
                    + "gelten als verschiedene Strecken.");
            }

            // Und die Gegenprobe: das Band darf nicht so weit sein, dass zwei
            // wirklich verschiedene Strecken zusammenfallen.
            if (Rivals.RecordedLap.SameCourse(6069f, 8000f))
            {
                throw new InvalidOperationException(
                    "6,1 km und 8,0 km gelten als dieselbe Strecke -- das Band ist "
                    + "zu weit, das Delta misst gegen einen fremden Kurs.");
            }

            // DER STARTPUNKT schlaegt die Laenge: er ist die einzige echte
            // Streckenkennung, die die Telemetrie hergibt.
            var linkeSchleife = new Rivals.RecordedLap
            {
                LengthMetres = 6000f, StartX = 1200f, StartZ = -430f,
            };
            var dieselbeLinie = new Rivals.RecordedLap
            {
                // Dieselbe Startlinie, eine andere Linie gefahren: 1,7 % kuerzer.
                //
                // Hier standen einmal 5 %. Das war richtig, solange die Laenge aus
                // `DistanceTraveled` kam -- die schwankte auf derselben Strecke so
                // stark. Aus dem gefahrenen Weg gerechnet ist sie stabil: zwei
                // Fahrten bis zum selben Punkt lagen am 2026-09-13 nur 0,6 %
                // auseinander. Eine andere Linie kostet kaum Weg.
                LengthMetres = 5900f, StartX = 1240f, StartZ = -455f,
            };
            var andererKurs = new Rivals.RecordedLap
            {
                // Auf den Meter gleich lang, aber woanders gestartet: NICHT dieselbe.
                LengthMetres = 6000f, StartX = 4800f, StartZ = 2100f,
            };
            if (!Rivals.RecordedLap.SameCourse(linkeSchleife, dieselbeLinie))
            {
                throw new InvalidOperationException(
                    "Zwei Runden von derselben Startlinie gelten als verschiedene "
                    + "Strecken, nur weil die gefahrene Linie 5 % kuerzer war.");
            }
            if (Rivals.RecordedLap.SameCourse(linkeSchleife, andererKurs))
            {
                throw new InvalidOperationException(
                    "Zwei Runden mit gleicher Laenge, aber 4 km auseinander "
                    + "liegenden Startlinien gelten als dieselbe Strecke -- dann "
                    + "misst das Delta gegen einen fremden Kurs.");
            }

            // UND DER GEMESSENE ABBRUCH (2026-09-13): 5.909 m statt 6.474 m auf
            // derselben Linie, 8,7 % kuerzer. Das ist keine andere Fahrweise, das
            // ist ein Abbruch 522 m vor dem Ziel -- pro Meter war er sogar
            // langsamer (15,1 gegen 14,9 ms). Galt er als dieselbe Strecke, hat er
            // als vermeintlich "schnellere" Gesamtzeit die vollstaendige Bestzeit
            // aus dem Bestand verdraengt, und jede spaetere Fahrt lief auf den
            // letzten 522 m ins Leere.
            var abbruch = new Rivals.RecordedLap
            {
                LengthMetres = 5909f, StartX = 1200f, StartZ = -430f,
            };
            var vollstaendig = new Rivals.RecordedLap
            {
                LengthMetres = 6474f, StartX = 1200f, StartZ = -430f,
            };
            if (Rivals.RecordedLap.SameCourse(abbruch, vollstaendig))
            {
                throw new InvalidOperationException(
                    "Ein Abbruch 522 m vor dem Ziel gilt als dieselbe Fahrt wie die "
                    + "vollstaendige -- dann verdraengt er sie als 'schnellere'.");
            }

            // Zwei Runden im Band, aber mit unterschiedlicher Laenge: der Bestand
            // darf daraus keine zwei Eintraege machen.
            var kurz = new Rivals.RecordedLap
            {
                LapSeconds = 83.706f, LengthMetres = 5949f,
                CarOrdinal = 4231, PerformanceIndex = 900, CarClass = 5,
                Drivetrain = 1, Cylinders = 8, MaxRpm = 7000, IdleRpm = 900,
                Samples = new List<Rivals.LapSample>
                {
                    new(0f, 0f), new(2974f, 41.8f), new(5949f, 83.706f),
                },
            };
            var lang = new Rivals.RecordedLap
            {
                LapSeconds = 84.730f, LengthMetres = 6069f,
                CarOrdinal = 4231, PerformanceIndex = 900, CarClass = 5,
                Drivetrain = 1, Cylinders = 8, MaxRpm = 7000, IdleRpm = 900,
                Samples = new List<Rivals.LapSample>
                {
                    new(0f, 0f), new(3034f, 42.4f), new(6069f, 84.730f),
                },
            };
            // RUNDEN OHNE KOORDINATEN BLEIBEN GETRENNT -- und das ist Absicht.
            //
            // Diese beiden stammen aus der Zeit, als die Laenge aus
            // `DistanceTraveled` kam: 6.069 m und 5.949 m, angeblich dieselbe
            // Strecke. Der Unterschied war ein Messfehler des Feldes, keine andere
            // Linie. Hier stand deshalb frueher die Forderung, dass sie
            // verschmelzen -- ueber ein Laengenband, und genau dieses Band hat
            // spaeter einen Abbruch zur Bestzeit gemacht.
            //
            // Seit die Strecke am Ort erkannt wird, koennen Runden OHNE Ort nicht
            // mehr zugeordnet werden. Sie bleiben einzeln stehen, statt geraten zu
            // werden; die alten Aufzeichnungen liegen ohnehin beiseite.
            var echt = new Rivals.LapLibrary(ablage + ".echt");
            try
            {
                echt.Add(lang);
                echt.Add(kurz);
                if (echt.Laps.Count != 2)
                {
                    throw new InvalidOperationException(
                        $"Zwei Runden ohne Koordinaten ergaben {echt.Laps.Count} "
                        + "Eintraege. Ohne Ort darf nichts zusammengelegt werden.");
                }
                if (Rivals.RecordedLap.SameRoad(lang, kurz))
                {
                    throw new InvalidOperationException(
                        "Zwei Runden ohne Koordinaten gelten als dieselbe Strasse -- "
                        + "das waere geraten, nicht gemessen.");
                }
            }
            finally
            {
                try { File.Delete(ablage + ".echt"); } catch (IOException) { }
            }

            // Und das Delta selbst: bei halber Strecke rund 2 s hinter der Bestzeit.
            var meins = langsam.SecondsAt(laenge / 2)!.Value;
            var bestes = referenz.SecondsAt(laenge / 2)!.Value;
            AssertNear(meins - bestes, 1000f * (0.040f - 0.036f), "Delta bei halber Strecke");
        }
        finally
        {
            try { File.Delete(ablage); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Ein Sprint hat keine Runden -- und muss trotzdem aufgezeichnet werden.
    /// </summary>
    /// <remarks>
    /// GEMESSEN am 2026-09-12/13: in drei Punkt-zu-Punkt-Rennen blieb `LapNumber`
    /// ueber 312 protokollierte Sekunden auf 0. Die Aufzeichnung legte damals nur
    /// beim Rundenwechsel ab -- also nie. Eine Fahrt ueber 6.579 m in 139 s war
    /// danach spurlos weg, und in Horizon ist das die Mehrzahl aller Rennen.
    ///
    /// Geprueft wird deshalb die Marke, an der ein Sprint wirklich endet: das Rennen
    /// hoert auf (`IsRaceOn` faellt auf 0), ohne dass je eine Rundenlinie kam.
    /// </remarks>
    /// <summary>
    /// Die Live-Karte beginnt je Runde neu -- und ein Sprung ist keine gefahrene Linie.
    /// </summary>
    /// <remarks>
    /// Gemeldet am 2026-09-25: beim stehenden Start eine lange Linie vom Ort vor dem
    /// Rennen zur Startlinie, und in Rennen 2 und 3 einer Meisterschaft die Strecke
    /// von Rennen 1. Beides, weil die Karte nur "Runde abgeschlossen" kannte.
    /// </remarks>
    private static void CheckLiveMapResets()
    {
        // ---- der Aufzeichner zaehlt jeden Neubeginn: Einfahrt, "GO", naechstes Rennen
        var aufnahme = new Rivals.LapRecorder();
        void Paket(int raceOn, int runde, float x, float uhr)
        {
            if (!ForzaPacket.TryParse(LapPacket(raceOn, runde, x, uhr, 0f, 1234, 700, 5, x: x), out var paket))
            {
                throw new InvalidOperationException("Das Kartenpaket parste nicht.");
            }
            aufnahme.OnTelemetry(paket);
        }
        // Rennen 1: das Auto rollt durch die Einfahrt, die Uhr laeuft schon ...
        for (var s = 0.2f; s < 4.3f; s += 0.1f) { Paket(1, 0, 5000f + (s * 10f), s); }
        var vorGo = aufnahme.LapGeneration;
        // ... und beim "GO" springt sie zurueck -- ab hier ist es die Runde.
        Paket(1, 0, 5043f, 0.02f);
        if (aufnahme.LapGeneration == vorGo)
        {
            throw new InvalidOperationException(
                "Live-Karte: das \"GO\" beginnt keine neue Spur -- die Einfahrt bliebe als Linie stehen.");
        }
        for (var s = 0.1f; s < 30f; s += 0.1f) { Paket(1, 0, 5043f + (s * 30f), s); }
        // Ziel, Ergebnisschirm, und Rennen 2 auf einer ganz anderen Strecke.
        Paket(0, 0, 5943f, 30f);
        var nachRennen1 = aufnahme.LapGeneration;
        for (var s = 0.05f; s < 2f; s += 0.1f) { Paket(1, 0, -8000f + (s * 20f), s); }
        if (aufnahme.LapGeneration == nachRennen1)
        {
            throw new InvalidOperationException(
                "Live-Karte: Rennen 2 beginnt keine neue Runde -- die Karte bliebe auf Rennen 1.");
        }

        // ---- die Spur: ein Sprung schneidet ab (Rewind) oder beginnt neu (Szenenwechsel)
        var spur = new List<PointF>();
        for (var i = 0; i <= 100; i++) { Rivals.LiveMapHud.SpurFortsetzen(spur, new PointF(i * 10f, 0f)); }
        var voll = spur.Count;
        Rivals.LiveMapHud.SpurFortsetzen(spur, new PointF(400f, 5f));
        if (spur.Count >= voll || Math.Abs(spur[^1].X - 400f) > 11f)
        {
            throw new InvalidOperationException(
                "Live-Karte: nach einem Zurueckspulen bleibt die Spur hinter dem Auto stehen.");
        }
        Rivals.LiveMapHud.SpurFortsetzen(spur, new PointF(-9000f, 7000f));
        if (spur.Count != 1)
        {
            throw new InvalidOperationException(
                $"Live-Karte: nach einem Sprung ans andere Ende der Karte haengen noch {spur.Count - 1} alte Punkte "
                + "an der Spur -- genau das war die lange Linie zur Startlinie.");
        }
        Rivals.LiveMapHud.SpurFortsetzen(spur, new PointF(-8995f, 7000f));
        Rivals.LiveMapHud.SpurFortsetzen(spur, new PointF(-8990f, 7000f));
        if (spur.Count != 3)
        {
            throw new InvalidOperationException("Live-Karte: gewoehnliches Fahren nach einem Sprung wird nicht mehr gezeichnet.");
        }
    }

    private static void CheckSprintRecording()
    {
        var aufnahme = new Rivals.LapRecorder();
        var fertige = new List<Rivals.RecordedLap>();
        aufnahme.LapCompleted += (_, lap) => fertige.Add(lap);

        // 3.000 m geradeaus in 100 s, Rundennummer durchgehend 0.
        for (var m = 0f; m <= 3000f; m += 2f)
        {
            if (!ForzaPacket.TryParse(LapPacket(
                    1, 0, m, m / 30f, 0f, 1234, 700, 5), out var paket))
            {
                throw new InvalidOperationException("Das Sprintpaket parste nicht.");
            }
            aufnahme.OnTelemetry(paket);
        }
        if (fertige.Count != 0)
        {
            throw new InvalidOperationException(
                "Waehrend der Fahrt wurde schon etwas abgelegt.");
        }

        // Ziel: das Rennen endet, ohne dass je eine Rundenlinie kam.
        if (!ForzaPacket.TryParse(LapPacket(
                0, 0, 3000f, 100f, 0f, 1234, 700, 5), out var schluss))
        {
            throw new InvalidOperationException("Das Schlusspaket parste nicht.");
        }
        aufnahme.OnTelemetry(schluss);

        if (fertige.Count != 1)
        {
            throw new InvalidOperationException(
                $"Ein beendeter Sprint ergab {fertige.Count} Aufzeichnungen statt einer "
                + "-- genau so gingen am 2026-09-12 drei Rennen verloren.");
        }
        var fahrt = fertige[0];
        if (!fahrt.EndedAtFinish)
        {
            throw new InvalidOperationException(
                "Die Fahrt ist nicht als am Ziel beendet vermerkt.");
        }
        AssertNear(fahrt.LengthMetres, 3000f, "Sprintlaenge");
        AssertNear(fahrt.LapSeconds, 100f, "Sprintdauer");
        if (fahrt.CarOrdinal != 1234 || fahrt.PerformanceIndex != 700)
        {
            throw new InvalidOperationException(
                $"Die Fahrzeugdaten gingen beim Zieleinlauf verloren: Auto "
                + $"{fahrt.CarOrdinal}, PI {fahrt.PerformanceIndex}. Beim Rennende "
                + "sind die Paketfelder nicht mehr befuellt -- gemerkt gehoeren sie.");
        }

        // Und eine Fahrt, die sofort abgebrochen wird, ist keine Fahrt.
        var kurz = new Rivals.LapRecorder();
        var kurzListe = new List<Rivals.RecordedLap>();
        kurz.LapCompleted += (_, lap) => kurzListe.Add(lap);
        for (var m = 0f; m <= 100f; m += 2f)
        {
            if (ForzaPacket.TryParse(LapPacket(1, 0, m, m / 30f, 0f, 1234, 700, 5),
                                     out var paket))
            {
                kurz.OnTelemetry(paket);
            }
        }
        if (ForzaPacket.TryParse(LapPacket(0, 0, 100f, 3.3f, 0f, 1234, 700, 5),
                                 out var aus))
        {
            kurz.OnTelemetry(aus);
        }
        if (kurzListe.Count != 0)
        {
            throw new InvalidOperationException(
                "Ein Abbruch nach 100 m wurde als Fahrt abgelegt.");
        }
    }

    /// <summary>
    /// Das Delta muss am ORT messen, nicht nach gefahrenen Metern.
    /// </summary>
    /// <remarks>
    /// DER FALL, DER ES ENTSCHEIDET (vom Nutzer benannt, 2026-09-12): ueber eine
    /// Klippe fliegen, ausritt, abkuerzen. Dann ist die zurueckgelegte Strecke eine
    /// voellig andere als die der Bestzeit am selben Punkt -- und ein Vergleich
    /// "nach denselben Metern" sagt dann etwas ueber zwei verschiedene Stellen der
    /// Strecke aus. Also: keine Auskunft ohne Ortsbezug.
    /// </remarks>
    private static void CheckPositionDelta()
    {
        // Eine Referenzrunde als Gerade: 1000 m, 10 s, alle 5 m ein Punkt.
        var punkte = new List<Rivals.LapSample>();
        for (var m = 0; m <= 1000; m += 5)
        {
            punkte.Add(new Rivals.LapSample(m, m * 0.01f, m, 0f, 0f));
        }
        var referenz = new Rivals.RecordedLap
        {
            LapSeconds = 10f, LengthMetres = 1000f, Samples = punkte,
        };
        if (!referenz.HasPositions)
        {
            throw new InvalidOperationException(
                "Eine Runde mit Koordinaten gilt als eine ohne.");
        }

        var hint = 0;
        var beiHalb = referenz.SecondsAtPosition(500f, 0f, 0f, ref hint, out var weg);
        if (beiHalb is null) { throw new InvalidOperationException("Kein Treffer auf der Linie."); }
        AssertNear(beiHalb.Value, 5f, "Zeit am Ort");
        AssertNear(weg, 0f, "Abstand zur Linie");

        // Zwischen zwei Messpunkten: die Zeit wird geteilt, nicht gerundet.
        hint = 0;
        var dazwischen = referenz.SecondsAtPosition(502.5f, 0f, 0f, ref hint, out _);
        AssertNear(dazwischen!.Value, 5.025f, "Zeit zwischen zwei Messpunkten");

        // Ein paar Meter neben der Linie zaehlt weiterhin als dieselbe Stelle.
        hint = 0;
        var daneben = referenz.SecondsAtPosition(500f, 0f, 4f, ref hint, out var seitlich);
        AssertNear(daneben!.Value, 5f, "Zeit knapp neben der Linie");
        AssertNear(seitlich, 4f, "seitlicher Abstand");

        // DER KLIPPENFALL: 700 m gefahren, aber erst bei 500 m der Strecke. Nach
        // Metern kaeme 7 s heraus, richtig sind 5 s.
        hint = 0;
        var nachOrt = referenz.SecondsAtPosition(500f, 0f, 0f, ref hint, out _)!.Value;
        var nachMetern = referenz.SecondsAt(700f)!.Value;
        AssertNear(nachOrt, 5f, "Umweg: Zeit am Ort");
        AssertNear(nachMetern, 7f, "Umweg: Zeit nach Metern");
        if (Math.Abs(nachOrt - nachMetern) < 1.5f)
        {
            throw new InvalidOperationException(
                "Ort und Meter liefern dasselbe -- dann prueft dieser Test nichts.");
        }

        // HINTER DEM ENDE DER REFERENZ: keine Zeit, nicht die letzte.
        //
        // GEMESSEN am 2026-09-13: die eigene Fahrt rollte 71 m weiter aus als die
        // Referenz. Die Suche blieb auf deren letztem Segment stehen, die
        // Referenzzeit war eingefroren -- und das Delta zaehlte von da an nur noch
        // die eigene Uhr hoch, glatt von -705 ms auf +121 ms. Auf dem Schirm sah
        // das aus wie ein verlorener Vorsprung im Ziel.
        hint = referenz.Samples.Count - 2;
        referenz.SecondsAtPosition(1400f, 0f, 0f, ref hint, out var wieWeit, out _,
                                   out var dahinter);
        if (!dahinter)
        {
            throw new InvalidOperationException(
                "400 m hinter dem Ende der Referenz wurde nicht als 'am Ende' "
                + "gemeldet. Dann zaehlt das Delta die eigene Uhr hoch und sieht "
                + "aus wie ein einbrechender Vorsprung.");
        }
        if (wieWeit < 300f)
        {
            throw new InvalidOperationException(
                $"400 m hinter dem Ende wurden als {wieWeit:0} m Abstand gemeldet.");
        }

        // Kurz VOR dem Ende gibt es dagegen eine Zeit, und zwar ohne Flagge.
        hint = 0;
        var kurzVorSchluss = referenz.SecondsAtPosition(995f, 0f, 0f, ref hint,
                                                        out _, out _, out var amSchluss);
        if (kurzVorSchluss is null || amSchluss)
        {
            throw new InvalidOperationException(
                "Fuenf Meter vor dem Ende der Referenz galt als 'am Ende' -- dann "
                + "faellt das Delta schon vor dem Ziel aus.");
        }

        // Und GENAU am Ziel muss die Zeit da sein: daran entscheidet sich, wer
        // weiter gekommen ist.
        hint = 0;
        if (referenz.SecondsAtPosition(1000f, 0f, 0f, ref hint, out _) is null)
        {
            throw new InvalidOperationException(
                "Am Ziel der Referenz gab es keine Zeit. Dann kann keine Fahrt "
                + "belegen, dass sie bis dorthin gekommen ist.");
        }

        // Weit weg von der Strecke: die Entfernung muss das melden, damit der
        // Streifen schweigen kann, statt eine Zahl zu erfinden.
        hint = 0;
        referenz.SecondsAtPosition(500f, 0f, 400f, ref hint, out var weitWeg);
        if (weitWeg < 100f)
        {
            throw new InvalidOperationException(
                $"400 m neben der Strecke wurden als {weitWeg:0} m gemeldet.");
        }

        // Die EINGABEN der Bestzeit an meiner Stelle -- dafuer sind die Spuren da.
        //
        // Gefragt ist "was hat die Bestzeit HIER gemacht", nicht "was hat sie vor
        // ebenso vielen Sekunden gemacht": zwei Runden sind an derselben Uhrzeit
        // verschieden weit gekommen.
        var mitGas = new List<Rivals.LapSample>
        {
            new(0f, 0f, 0f, 0f, 0f, 50f, 1f, 0f, 0f, 0f, 0f, 0f, 0f),
            new(100f, 1f, 100f, 0f, 0f, 50f, 0f, 1f, 0.25f, 0f, 0f, 0.5f, 0f),
        };
        var mitEingaben = new Rivals.RecordedLap
        {
            LapSeconds = 1f, LengthMetres = 100f, Samples = mitGas,
        };
        hint = 0;
        var zeitDort = mitEingaben.SecondsAtPosition(50f, 0f, 0f, ref hint,
                                                    out _, out var dort);
        if (zeitDort is null) { throw new InvalidOperationException("Keine Stelle gefunden."); }
        AssertNear(dort.Throttle, 0.5f, "Gas der Bestzeit an dieser Stelle");
        AssertNear(dort.Brake, 0.5f, "Bremse der Bestzeit an dieser Stelle");
        AssertNear(dort.Clutch, 0.25f, "Kupplung der Bestzeit an dieser Stelle");
        AssertNear(dort.Steer, 0.125f, "Lenkung der Bestzeit an dieser Stelle");

        // Die eigene Spur haelt nur die eingestellte Spanne.
        var spur = new Rivals.InputTrace();
        for (var i = 0; i < 200; i++) { spur.Push(1f, 0f, 0f, 0f, 4f, 2); }
        if (spur.Mine(0).Count > 30)
        {
            throw new InvalidOperationException(
                $"Zwei Sekunden Spur ergaben {spur.Mine(0).Count} Punkte -- die Spur "
                + "waechst unbegrenzt.");
        }

        // DER VORLAUF: die Bestzeit muss sich auch VOR dem jetzigen Punkt abfragen
        // lassen, sonst gibt es nichts nachzufahren.
        var kommt = mitEingaben.SampleAtSeconds(0.75f);
        if (kommt is null)
        {
            throw new InvalidOperationException(
                "Die Bestzeit liefert keinen Punkt in der Zukunft -- dann kann die "
                + "Spur nicht zeigen, was gleich kommt.");
        }
        AssertNear(kommt.Value.Throttle, 0.25f, "Gas der Bestzeit in 0,75 s");
        AssertNear(kommt.Value.Brake, 0.75f, "Bremse der Bestzeit in 0,75 s");

        // DER GANG WIRD NICHT GETEILT. Zwischen dem dritten und dem vierten gibt es
        // keinen dreieinhalbten -- eine gemittelte Gangzahl waere eine Anzeige, die
        // es so im Auto nie gibt.
        var geschaltet = new Rivals.RecordedLap
        {
            LapSeconds = 2f, LengthMetres = 100f,
            Samples = new List<Rivals.LapSample>
            {
                new(0f, 0f, 0f, 0f, 0f, 30f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 3f),
                new(100f, 2f, 100f, 0f, 0f, 60f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 4f),
            },
        };
        var mitten = geschaltet.SampleAtSeconds(1f);
        if (mitten is null) { throw new InvalidOperationException("Kein Punkt in der Mitte."); }
        if (Math.Abs(mitten.Value.Gear - 3f) > 0.001f)
        {
            throw new InvalidOperationException(
                $"Zwischen dem 3. und 4. Gang kam {mitten.Value.Gear:0.00} heraus -- "
                + "der Gang darf nicht geteilt werden.");
        }
        // Die Geschwindigkeit dazwischen SCHON: die aendert sich stetig.
        AssertNear(mitten.Value.Speed, 45f, "Geschwindigkeit dazwischen");

        // Und ausserhalb der Runde wird nichts erfunden.
        if (mitEingaben.SampleAtSeconds(-0.5f) is not null
            || mitEingaben.SampleAtSeconds(5f) is not null)
        {
            throw new InvalidOperationException(
                "Vor dem Start oder nach dem Ziel kam ein Wert zurueck statt nichts.");
        }

        // Eine Runde ohne Koordinaten darf gar nicht erst am Ort gemessen werden.
        var alt = new Rivals.RecordedLap
        {
            LapSeconds = 10f, LengthMetres = 1000f,
            Samples = new List<Rivals.LapSample> { new(0f, 0f), new(500f, 5f), new(1000f, 10f) },
        };
        hint = 0;
        if (alt.SecondsAtPosition(500f, 0f, 0f, ref hint, out _) is not null)
        {
            throw new InvalidOperationException(
                "Fuer eine Runde ohne Koordinaten kam eine Zeit am Ort zurueck.");
        }

        // DIE KLASSENGRENZEN, an jeder Kante geprueft.
        //
        // Hier stand bis zum 2026-09-14 "800 ist A, 900 ist S1, 1000 ist X" -- und
        // das war um eine ganze Klasse verschoben. Die Klassenleiste des Spiels sagt
        // D 400 / C 500 / B 600 / A 700 / S1 800 / S2 900 / R 998; derselbe Text
        // steht seit Wochen in einem Kommentar in forza_navigator.ps1, und die
        // Garagen-Datenbank bestaetigt ihn ueber ClassID. Der Test hat den Fehler
        // nicht gefunden, weil er dieselbe falsche Annahme prueft wie der Code.
        //
        // Darum jetzt an den KANTEN: 400 ist noch D, 401 schon C.
        (int Pi, string Klasse)[] kanten =
        [
            (400, "D"), (401, "C"), (500, "C"), (501, "B"), (600, "B"), (601, "A"),
            (700, "A"), (701, "S1"), (800, "S1"), (801, "S2"), (900, "S2"),
            (901, "R"), (998, "R"),
        ];
        foreach (var (pi, klasse) in kanten)
        {
            var gelesen = Rivals.LapArchive.ClassOf(pi);
            if (gelesen != klasse)
            {
                throw new InvalidOperationException(
                    $"PI {pi} wurde als Klasse '{gelesen}' eingeordnet statt "
                    + $"'{klasse}'.");
            }
        }
        // Und der Bildschirmleser muss dieselben Grenzen kennen wie der Bestand --
        // zwei Massstaebe fuer dieselbe Frage waeren zwei Antworten.
        foreach (var (pi, klasse) in kanten)
        {
            var gelesen = Rivals.RivalsScreenReader.ClassForPi(pi);
            if (gelesen != klasse)
            {
                throw new InvalidOperationException(
                    $"Der Bildschirmleser ordnet PI {pi} als '{gelesen}' ein, der "
                    + $"Rundenbestand als '{klasse}'.");
            }
        }

        // Taste und Auswahlliste muessen DIESELBEN Stufen kennen.
        //
        // Sonst schaltet die Taste auf etwas, das der Reiter nicht anzeigen kann --
        // oder der Reiter bietet etwas an, das die Taste ueberspringt. Beides faellt
        // erst im Rennen auf, und dann ist es zu spaet zum Nachsehen.
        var perTaste = Rivals.OverlayController.DeltaModes;
        var perListe = Rivals.HudPartPanel.References.Select(r => r.Key).ToArray();
        foreach (var stufe in perTaste)
        {
            if (!perListe.Contains(stufe))
            {
                throw new InvalidOperationException(
                    $"Die Taste schaltet auf '{stufe}', die Auswahlliste kennt das nicht.");
            }
        }
        foreach (var stufe in perListe)
        {
            if (!perTaste.Contains(stufe))
            {
                throw new InvalidOperationException(
                    $"Die Auswahlliste bietet '{stufe}', die Taste ueberspringt es.");
            }
        }
        if (perTaste.Distinct().Count() != perTaste.Length)
        {
            throw new InvalidOperationException(
                "Eine Stufe steht zweimal in der Reihenfolge -- die Taste wuerde "
                + "haengen bleiben.");
        }

        // DIE STRASSE ENTSCHEIDET, NICHT DIE LAENGE.
        //
        // Drei Fahrten auf derselben Geraden: eine vollstaendige, eine engere Linie
        // (kuerzer, aber bis ans selbe Ziel) und ein Abbruch weit davor. In Metern
        // sind die letzten beiden kaum zu unterscheiden -- am erreichten ORT schon.
        Rivals.RecordedLap Strasse(float bis, float versatz, float sekundenJeMeter)
        {
            var punkte = new List<Rivals.LapSample>();
            for (var m = 0f; m <= bis; m += 5f)
            {
                punkte.Add(new Rivals.LapSample(m, m * sekundenJeMeter, m, 0f, versatz));
            }
            return new Rivals.RecordedLap
            {
                LapSeconds = bis * sekundenJeMeter, LengthMetres = bis,
                StartX = 0f, StartZ = versatz, Samples = punkte,
                CarOrdinal = 1234, PerformanceIndex = 700, CarClass = 5,
                Drivetrain = 1, Cylinders = 6, MaxRpm = 7000, IdleRpm = 900,
            };
        }

        var voll = Strasse(6000f, 0f, 0.0149f);        // 89,4 s bis zum Ziel
        var engereLinie = Strasse(6000f, 8f, 0.0146f); // 8 m seitlich, schneller
        var abbruch = Strasse(4000f, 0f, 0.0140f);     // endet 2 km vorher

        if (!Rivals.RecordedLap.SameRoad(voll, engereLinie))
        {
            throw new InvalidOperationException(
                "Acht Meter seitlich gelten als andere Strasse -- dann findet eine "
                + "andere Linie ihre eigene Bestzeit nicht wieder.");
        }
        if (!Rivals.RecordedLap.SameRoad(voll, abbruch))
        {
            throw new InvalidOperationException(
                "Ein Abbruch gilt als andere Strasse. Er ist dieselbe Strasse -- "
                + "nur nicht dieselbe Strecke.");
        }
        if (abbruch.ReachesEndOf(voll))
        {
            throw new InvalidOperationException(
                "Ein Abbruch bei 4 km erreicht angeblich das Ziel bei 6 km.");
        }
        if (!voll.ReachesEndOf(abbruch))
        {
            throw new InvalidOperationException(
                "Die vollstaendige Fahrt kommt angeblich nicht so weit wie der Abbruch.");
        }

        // DAS ZIEL WIRD AUS DEN FAHRTEN BESTIMMT, NICHT AUS EINER SCHWELLE.
        //
        // Vier Fahrten derselben Strecke: drei enden am Ziel (mit unterschiedlich
        // langem Nachlauf, wie ihn das Spiel erzeugt), eine bricht bei 60 % ab.
        // Die schnellste BIS ZUM ZIEL muss der Massstab werden -- nicht die mit der
        // laengsten Aufzeichnung und nicht die mit der kleinsten Gesamtzeit.
        var zielGruppe = new List<Rivals.RecordedLap>
        {
            Strasse(6000f, 0f, 0.0150f),   // 90,00 s bis 6.000 m
            Strasse(6080f, 0f, 0.0152f),   // langsamer, aber 80 m Nachlauf
            Strasse(6040f, 6f, 0.0148f),   // DIE SCHNELLSTE, 40 m Nachlauf
            Strasse(3600f, 0f, 0.0130f),   // Abbruch bei 60 %, waere "schnellste"
        };
        var route = zielGruppe[1];
        foreach (var k in zielGruppe)
        {
            if (k.RouteLength > route.RouteLength) { route = k; }
        }
        var zielBogen = Rivals.RecordedLap.FinishArc(route, zielGruppe);
        if (zielBogen < 5800f || zielBogen > 6200f)
        {
            throw new InvalidOperationException(
                $"Das Ziel wurde bei {zielBogen:0} m vermutet statt bei rund 6.000 m "
                + "-- dann zieht ein einzelner Nachlauf oder ein Abbruch es weg.");
        }

        var massstab = Rivals.LapLibrary.SchnellsteZumZiel(zielGruppe);
        if (massstab is null || Math.Abs(massstab.LapSeconds - 6040f * 0.0148f) > 0.5)
        {
            throw new InvalidOperationException(
                "Als Massstab kam nicht die schnellste Fahrt bis zum Ziel heraus, "
                + $"sondern {massstab?.LapSeconds:0.000} s / "
                + $"{massstab?.LengthMetres:0} m.");
        }

        // DER NACHLAUF (gemessen 2026-09-13): das Spiel meldet nach der Ziellinie
        // noch Zehntelsekunden lang "Rennen laeuft". Bei 320 km/h sind das bis zu
        // 80 m mehr Aufzeichnung -- bei voller Fahrt, ohne Ausrollen. Eine
        // langsamere Fahrt mit laengerem Nachlauf darf die schnellere NICHT
        // verdraengen; genau so ist die Bestzeit in dieser Nacht verschwunden und
        // der Streifen zeigte am Ziel -0,4 s statt +0,7 s.
        var schnellKurz = Strasse(6000f, 0f, 0.0149f);   // 89,40 s
        var langsamLang = Strasse(6080f, 0f, 0.0151f);   // 91,81 s, 80 m Nachlauf
        var nachlauf = new Rivals.LapLibrary(Path.Combine(Path.GetTempPath(),
            "forza-nachlauf-" + Guid.NewGuid().ToString("N") + ".json"));
        nachlauf.Add(schnellKurz);
        if (nachlauf.Add(langsamLang))
        {
            throw new InvalidOperationException(
                "Eine langsamere Fahrt mit 80 m Nachlauf hat die schnellere "
                + "verdraengt -- Aufzeichnungslaenge ist kein Fortschritt.");
        }
        AssertNear(nachlauf.Laps[0].LapSeconds, 6000f * 0.0149f,
                   "Bestzeit nach Nachlauf-Pruefung", 0.01);

        // Eine ECHTE Verlaengerung (400 m weiter) zaehlt dagegen sehr wohl.
        var wirklichWeiter = Strasse(6400f, 0f, 0.0152f);
        if (!nachlauf.Add(wirklichWeiter))
        {
            throw new InvalidOperationException(
                "Eine 400 m weiter fuehrende Fahrt wurde nicht uebernommen -- dann "
                + "bleibt die Strecke fuer immer zu kurz.");
        }

        var bestand = new Rivals.LapLibrary(Path.Combine(Path.GetTempPath(),
            "forza-strasse-" + Guid.NewGuid().ToString("N") + ".json"));
        bestand.Add(voll);
        if (bestand.Add(abbruch))
        {
            throw new InvalidOperationException(
                "Der Abbruch kam in den Bestand -- als 'schnellere' Gesamtzeit "
                + "verdraengt er dort die vollstaendige Fahrt.");
        }
        if (!bestand.Add(engereLinie))
        {
            throw new InvalidOperationException(
                "Die schnellere Fahrt auf derselben Strasse wurde nicht zum Massstab "
                + $"({bestand.Laps.Count} Eintraege).");
        }
        // Alle drei bleiben gespeichert -- erst aus mehreren Fahrten ergibt sich,
        // wo die Strecke endet. Massstab ist die schnellste BIS ZUM ZIEL.
        var massstabStrasse = Rivals.LapLibrary.SchnellsteZumZiel(bestand.Laps);
        AssertNear(massstabStrasse!.LapSeconds, 6000f * 0.0146f, "Massstab", 0.2);
        if (ReferenceEquals(massstabStrasse, abbruch))
        {
            throw new InvalidOperationException(
                "Der Abbruch wurde zum Massstab -- er erreicht das Ziel nie.");
        }

        // Das Archiv sortiert nach Strecke, Klasse, Auto, Abstimmung und Merkzettel.
        var wurzel = Path.Combine(Path.GetTempPath(),
            "forza-archiv-selbsttest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runde = new Rivals.RecordedLap
            {
                LapSeconds = 92.348f, LengthMetres = 1800f,
                CarOrdinal = 1200, PerformanceIndex = 800, CarClass = 4,
                Drivetrain = 2, Cylinders = 10, MaxRpm = 10000, IdleRpm = 1200,
                StartX = 1234f, StartZ = -567f,
                RecordedAt = DateTimeOffset.Now,
                Samples = punkte,
            };
            var pfad = Rivals.LapArchive.Save(runde, "regen", wurzel);
            if (pfad is null || !File.Exists(pfad))
            {
                throw new InvalidOperationException("Die Runde wurde nicht abgelegt.");
            }
            var teile = pfad.Substring(wurzel.Length).Split(
                Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (teile.Length != 6)
            {
                throw new InvalidOperationException(
                    "Erwartet: Strecke/Klasse/Auto/Abstimmung/Merkzettel/Datei, "
                    + $"bekommen: {string.Join("/", teile)}");
            }
            // PI 800 ist S1, nicht A. Bis zum 2026-09-14 stand hier "A", weil der
            // Bestand seine Klassengrenzen um eine Stufe zu niedrig zog.
            //
            // Und seit dem 2026-09-15 traegt der Ordnername auch die ZIELLINIE:
            // "course_1225_-575_to_...". Geprueft wird darum der Anfang, nicht die
            // ganze Zeichenkette -- das Ziel haengt an den Messpunkten dieses
            // Tests und ist hier nicht der Gegenstand.
            if (!teile[0].StartsWith("course_1225_-575", StringComparison.Ordinal)
                || !teile[0].Contains("_to_", StringComparison.Ordinal)
                || teile[1] != "S1"
                || teile[2] != "car1200" || teile[4] != "regen")
            {
                throw new InvalidOperationException(
                    $"Falsch einsortiert: {string.Join("/", teile)}");
            }
            if (!File.Exists(Path.Combine(wurzel, teile[0], "course.json")))
            {
                throw new InvalidOperationException(
                    "Zur Strecke wurde keine Notiz angelegt -- dann laesst sie sich "
                    + "nie benennen.");
            }
        }
        finally
        {
            try { Directory.Delete(wurzel, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>A ranking hundreds deep has to be walkable from a panel.</summary>
    /// <remarks>
    /// The panel shows the rows its own height can hold -- 44 of 474 on a 4K screen,
    /// which read as a ranking that ended at 44. It now takes the whole list and
    /// Page Down walks it, so what is checked here is that every row is reachable and
    /// that the walk stops at both ends instead of running off.
    ///
    /// A real window, drawn for real: the row count comes out of OnPaint measuring
    /// its own fonts, so a check that never paints would prove nothing.
    /// </remarks>
    /// <summary>
    /// Liest OwnCars den Bestand richtig aus dem PFAD?
    /// </summary>
    /// <remarks>
    /// Die Klasse oeffnet keine einzige Datei -- Klasse, Auto, Sekunden und die
    /// Kennzeichen stehen im Pfad, und tausend JSON-Dokumente im Zeichenpfad einer
    /// Overlay-Aktualisierung zu lesen waere Verschwendung. Der Preis dafuer ist,
    /// dass eine Aenderung an `LapArchive.Save` das Lesen STILL kaputtmacht: es
    /// kaeme keine Ausnahme, es stuende nur nichts mehr in der Spalte.
    ///
    /// Darum wird hier nicht von Hand ein Pfad gebastelt, sondern mit
    /// `LapArchive.Save` wirklich abgelegt und danach gelesen. Aendert sich die
    /// Ablage, faellt dieser Test -- und nicht der Nutzer.
    /// </remarks>
    /// <summary>
    /// Trennt die Kurs-Kennung zwei Strecken mit DERSELBEN Startlinie?
    /// </summary>
    /// <remarks>
    /// Bis zum 2026-09-15 nicht: die Kennung bestand nur aus dem Startpunkt. Im
    /// Bestand des Nutzers lagen dadurch in einem Ordner 56 Laeufe mit Zielen
    /// 3.725 m auseinander und Laengen von 906 bis 6.576 m -- und das Rundendelta
    /// verglich dort gegen eine fremde Strecke, ohne dass es auffiel.
    ///
    /// Der Hinweis kam vom Nutzer: Start UND Ziel bestimmen die Strecke. Dieser
    /// Test haelt das fest, weil der Fehler STILL war -- es gab keine Ausnahme,
    /// nur falsche Vergleiche.
    /// </remarks>
    /// <summary>
    /// Die volle Telemetriespur: Spaltennamen, Doppelte, Datei, Abgabe.
    /// </summary>
    /// <remarks>
    /// Der erste Teil ist der wichtigste. `ForzaPacket.Get` gibt fuer einen
    /// unbekannten Namen **0** zurueck statt eines Fehlers -- ein Tippfehler in der
    /// Spaltenliste erzeugte also eine Spalte voller Nullen, und die saehe beim
    /// Auswerten aus wie ein Sensor, der nichts liefert. Hier faellt sie sofort auf.
    /// </remarks>
    private static void CheckTelemetryTrack()
    {
        if (Rivals.TelemetryTrack.UnknownColumns.Count > 0)
        {
            throw new InvalidOperationException(
                "Diese Spalten kennt das Paket nicht (sie waeren still 0): "
                + string.Join(", ", Rivals.TelemetryTrack.UnknownColumns));
        }

        var spur = new Rivals.TelemetryTrack();
        var paket = new byte[324];
        BitConverter.GetBytes(1).CopyTo(paket, 0);

        // Zwei Pakete mit DERSELBEN Spielzeit sind dasselbe Paket.
        BitConverter.GetBytes(1000u).CopyTo(paket, 4);
        if (!ForzaPacket.TryParse(paket, out var a))
        {
            throw new InvalidOperationException("Das Testpaket parste nicht.");
        }
        if (!spur.Add(a, 1f, 10f))
        {
            throw new InvalidOperationException("Der erste Datensatz wurde verworfen.");
        }
        if (spur.Add(a, 1.02f, 10.5f))
        {
            throw new InvalidOperationException("Dasselbe Paket wurde zweimal aufgenommen.");
        }
        if (spur.Duplicates != 1)
        {
            throw new InvalidOperationException(
                $"Doppelte gezaehlt: {spur.Duplicates}, erwartet 1.");
        }

        // Eine neue Spielzeit ist eine neue Messung -- auch bei gleichem Inhalt.
        // Ein stehendes Auto schickt vollkommen gleiche Pakete, und die sind
        // trotzdem verschiedene Messungen.
        BitConverter.GetBytes(1016u).CopyTo(paket, 4);
        if (!ForzaPacket.TryParse(paket, out var b))
        {
            throw new InvalidOperationException("Das zweite Testpaket parste nicht.");
        }
        if (!spur.Add(b, 1.02f, 10.5f))
        {
            throw new InvalidOperationException("Ein neues Paket wurde faelschlich verworfen.");
        }
        if (spur.Count != 2)
        {
            throw new InvalidOperationException($"{spur.Count} Datensaetze, erwartet 2.");
        }

        // Schreiben, wieder einlesen, nachsehen ob dasselbe herauskommt.
        var datei = Path.Combine(Path.GetTempPath(),
                                 "forza-tele-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var geschrieben = spur.Save(datei);
        try
        {
            if (geschrieben is null || !File.Exists(geschrieben))
            {
                throw new InvalidOperationException("Die Spur wurde nicht geschrieben.");
            }
            string text;
            using (var roh = File.OpenRead(geschrieben))
            using (var entpackt = new System.IO.Compression.GZipStream(
                       roh, System.IO.Compression.CompressionMode.Decompress))
            using (var leser = new StreamReader(entpackt))
            {
                text = leser.ReadToEnd();
            }
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.GetProperty("rows").GetInt32() != 2)
            {
                throw new InvalidOperationException("Die Datei nennt nicht zwei Datensaetze.");
            }
            if (doc.RootElement.GetProperty("columns").GetArrayLength()
                != Rivals.TelemetryTrack.Columns.Length)
            {
                throw new InvalidOperationException("Der Spaltenkopf passt nicht zur Liste.");
            }
            if (doc.RootElement.GetProperty("data").GetArrayLength() != 2)
            {
                throw new InvalidOperationException("Es stehen nicht zwei Zahlenreihen darin.");
            }
            // Punkt als Dezimaltrennzeichen, unabhaengig von der Spracheinstellung:
            // "1,02" waere in einer Zahlenreihe zwei Werte statt einem.
            if (text.Contains("1,02", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Komma als Dezimaltrennzeichen -- die Zahlenreihe waere unlesbar.");
            }
        }
        finally
        {
            try { if (geschrieben is not null) { File.Delete(geschrieben); } }
            catch (Exception) { }
        }

        // Abgeben nimmt alles mit und laesst die Quelle leer zurueck.
        var abgegeben = spur.Detach();
        if (abgegeben.Count != 2 || spur.Count != 0)
        {
            throw new InvalidOperationException(
                $"Abgeben stimmt nicht: abgegeben {abgegeben.Count}, zurueck {spur.Count}.");
        }
    }

    /// <summary>
    /// Laesst sich eine Verknuepfung wirklich anlegen?
    /// </summary>
    /// <remarks>
    /// Geschrieben wird in einen Testordner und NICHT auf den Schreibtisch: ein
    /// Test, der den Schreibtisch des Nutzers vollstellt, wird beim ersten Mal
    /// abgeschaltet und prueft danach nie wieder etwas.
    ///
    /// Geprueft wird der ganze Weg ueber COM -- `WScript.Shell` ist zwar in jedem
    /// Windows vorhanden, aber ueber Reflexion angesprochen faellt ein falscher
    /// Feldname erst zur Laufzeit auf, und dann beim Nutzer.
    /// </remarks>
    /// <summary>
    /// Kommen Autonotiz und Umriss-Streifen wirklich AUF DEN SCHIRM?
    /// </summary>
    /// <remarks>
    /// Bis zum 2026-09-25 waren beide nie zu sehen: WS_EX_LAYERED ohne
    /// UpdateLayeredWindow. Jeder Test zeichnete sie in ein Bild und war gruen. Hier
    /// wird das Fenster gezeigt -- gut 0,2 s, in grellen Probefarben, ausnahmsweise
    /// OHNE Aufnahme-Ausblendung -- und der Schirm an seiner Stelle vorher und
    /// nachher fotografiert. Ohne Desktop (gesperrte Sitzung) laesst sich nichts
    /// fotografieren; dann wird die Pruefung uebersprungen, nicht bestanden.
    /// </remarks>
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h,
                                      IntPtr hdcSrc, int x1, int y1, int rop);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDC")]
    private static extern IntPtr BildschirmDc(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ReleaseDC")]
    private static extern int BildschirmDcFrei(IntPtr hWnd, IntPtr hDC);

    private static void CheckOverlaysReachTheScreen()
    {
        var flaeche = Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty;
        if (flaeche.Width < 640) { return; }
        // NUR AUF AUSDRUECKLICHEN WUNSCH (FORZA_SCREEN_TEST=1), und nie ueber einem
        // Vollbild. Am 2026-09-25 zweimal gelernt: einmal lief Forza, einmal
        // Counter-Strike -- beide Male legte der Selbsttest grelle Kaesten ueber ein
        // laufendes Spiel, und die Aufnahme zeigte ohnehin nur das Spiel, nicht die
        // Fenster darueber. Gemessen wurde also nichts, gestoert wurde der Spieler.
        if (Environment.GetEnvironmentVariable("FORZA_SCREEN_TEST") != "1")
        {
            Rivals.OverlayController.WriteDiagnostic(
                "Selbsttest Overlay: nicht verlangt (FORZA_SCREEN_TEST=1 schaltet die Pruefung auf dem Schirm ein)");
            return;
        }
        if (GameArea.VollbildVorne(out var wer))
        {
            Rivals.OverlayController.WriteDiagnostic(
                $"Selbsttest Overlay: UEBERSPRUNGEN -- {wer} fuellt den Schirm, eine Aufnahme zeigt dann keine Fenster darueber");
            return;
        }
        var s = new Rivals.OverlaySettings
        {
            CarNoteBack = "#00ff00", CarNoteBackAlpha = 255, CarNoteInk = "#ff00ff",
            CourseShapeBack = "#00ff00", CourseShapeBackAlpha = 255,
        };

        Bitmap? Foto(Rectangle wo)
        {
            // BitBlt MIT CAPTUREBLT, direkt ueber Win32: ohne das Flag laesst BitBlt
            // geschichtete Fenster -- genau die, um die es hier geht -- weg, und
            // Graphics.CopyFromScreen nimmt die Kombination gar nicht an
            // (InvalidEnumArgumentException). Der erste Entwurf dieser Pruefung wurde
            // dadurch jedes Mal still uebersprungen und "bestand".
            var b = new Bitmap(Math.Max(1, wo.Width), Math.Max(1, wo.Height));
            var schirm = BildschirmDc(IntPtr.Zero);
            try
            {
                using var g = Graphics.FromImage(b);
                var ziel = g.GetHdc();
                bool gelungen;
                try { gelungen = BitBlt(ziel, 0, 0, b.Width, b.Height, schirm, wo.X, wo.Y, 0x00CC0020 | 0x40000000); }
                finally { g.ReleaseHdc(ziel); }
                if (gelungen) { return b; }
            }
            finally { BildschirmDcFrei(IntPtr.Zero, schirm); }
            // Nur ein gescheitertes BitBlt heisst "kein Desktop" -- das wird gesagt.
            Rivals.OverlayController.WriteDiagnostic("Selbsttest Overlay: BitBlt scheiterte -- kein Desktop?");
            b.Dispose();
            return null;
        }

        // GEZAEHLT WIRD DIE PROBEFARBE, nicht "irgendetwas hat sich veraendert": ein
        // Spiel im Hintergrund bewegt sich, und seine Bewegung allein reichte dem
        // ersten Entwurf zum Bestehen. Reines Gruen (#00ff00) zeigt kein Spielmenue.
        static double Anteil(Bitmap vorher, Bitmap b)
        {
            var gruen = 0;
            var alle = 0;
            for (var y = 0; y < b.Height; y += 3)
            {
                for (var x = 0; x < b.Width; x += 3)
                {
                    var q = b.GetPixel(x, y);
                    var p = vorher.GetPixel(x, y);
                    alle++;
                    var istGruen = q.G > 225 && q.R < 40 && q.B < 40;
                    var warGruen = p.G > 225 && p.R < 40 && p.B < 40;
                    if (istGruen && !warGruen) { gruen++; }
                }
            }
            return alle == 0 ? 0 : (double)gruen / alle;
        }

        void Pruefe(Rivals.LayeredHud hud, Rectangle blockAufFlaeche, string was)
        {
            var block = blockAufFlaeche;
            block.Offset(flaeche.Location);
            using var vorher = Foto(block);
            if (vorher is null)
            {
                Rivals.OverlayController.WriteDiagnostic($"Selbsttest Overlay: {was} UEBERSPRUNGEN (keine Aufnahme)");
                return;
            }
            hud.AllowCaptureForTest();
            hud.Show();
            // Bis zu 1,5 s warten: der Fenstermanager setzt ein neues Fenster nicht im
            // selben Augenblick zusammen, und ein fester Wert schwankte (einmal 4 %,
            // beim naechsten Lauf bestanden).
            Bitmap? nachher = null;
            for (var i = 0; i < 30; i++)
            {
                Application.DoEvents();
                Thread.Sleep(50);
                nachher?.Dispose();
                nachher = Foto(block);
                if (nachher is null || Anteil(vorher, nachher) >= 0.5) { break; }
            }
            using var _ = nachher;
            var fenster = hud.Bounds;
            var sichtbar = hud.Visible;
            hud.Hide();
            Application.DoEvents();
            if (nachher is null)
            {
                Rivals.OverlayController.WriteDiagnostic($"Selbsttest Overlay: {was} UEBERSPRUNGEN (keine Aufnahme)");
                return;
            }
            var anteil = Anteil(vorher, nachher);
            Rivals.OverlayController.WriteDiagnostic(
                $"Selbsttest Overlay: {was} {anteil:P0} des Blocks in Probefarbe, Block {block}, Fenster {fenster}");
            if (hud.LastPushFailed || anteil < 0.5)
            {
                // Die beiden Aufnahmen liegen daneben -- ein "nicht zu sehen" ohne Bild
                // waere hier genau so wenig zu beweisen wie im Spiel.
                var ordner = Path.Combine(Path.GetTempPath(), "forza-overlay");
                Directory.CreateDirectory(ordner);
                vorher.Save(Path.Combine(ordner, $"selftest-{hud.GetType().Name}-vorher.png"));
                nachher.Save(Path.Combine(ordner, $"selftest-{hud.GetType().Name}-nachher.png"));
                throw new InvalidOperationException(
                    $"{was} kommt nicht auf den Schirm: nur {anteil:P0} des Blocks veraendert"
                    + (hud.LastPushFailed ? ", UpdateLayeredWindow scheiterte" : "")
                    + $"; Block {block}, Fenster {fenster}, sichtbar {sichtbar}, Schirm {flaeche}");
            }
        }

        using (var note = new Rivals.CarNoteHud(s, flaeche))
        {
            note.SetNote("Selbsttest", "overlay visibility check");
            using var bm = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bm);
            var kasten = Rectangle.Ceiling(Rivals.CarNoteHud.Lege(
                g, s, flaeche.Size, "Selbsttest", "overlay visibility check").Kasten);
            Pruefe(note, kasten, "Die Autonotiz");
        }
        using (var umriss = new Rivals.CourseShapeHud(s, flaeche))
        {
            umriss.SetCourses(new List<(string, Rivals.CourseShape.Outline?)> { ("Selbsttest", null) });
            Pruefe(umriss, Rivals.CourseShapeHud.Lege(s, flaeche.Size, 1).Block, "Der Umriss-Streifen");
        }
        using (var karte = new Rivals.LiveMapHud(new Rivals.OverlaySettings
               {
                   LiveMap = true, CourseShapeBack = "#00ff00", CourseShapeBackAlpha = 255,
               }, flaeche))
        {
            karte.SetSample(new List<PointF> { new(0, 0), new(1, 0), new(1, 1), new(0, 1) }, 1);
            Pruefe(karte, Rivals.LiveMapHud.Lege(new Rivals.OverlaySettings(), flaeche.Size), "Die Live-Karte");
        }
    }

    /// <summary>
    /// Faellt der Server-Client (ServerHttp) bei einem gescheiterten TLS-Handschlag
    /// auf HTTP zurueck -- und NUR dann?
    /// </summary>
    /// <remarks>
    /// Ein Ersatzserver auf 127.0.0.1 beantwortet den TLS-Handschlag mit Klartext,
    /// genau wie ein Rechner, der das Zertifikat nicht pruefen kann, ihn scheitern
    /// sieht. Erwartet: die Einreichung (POST mit Inhalt) kommt beim zweiten Anlauf
    /// ueber HTTP vollstaendig an, die naechste Anfrage geht gleich ueber HTTP, es gab
    /// genau EINEN TLS-Versuch -- und ein geschlossener Port faellt NICHT zurueck.
    /// </remarks>
    private static void CheckServerFallback()
    {
        var hoerer = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        hoerer.Start();
        var port = ((System.Net.IPEndPoint)hoerer.LocalEndpoint).Port;
        int tlsVersuche = 0, bedient = 0;
        var inhaltAngekommen = false;
        var faden = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    using var verbindung = hoerer.AcceptTcpClient();
                    verbindung.ReceiveTimeout = 5000;
                    var strom = verbindung.GetStream();
                    var puffer = new byte[16384];
                    var n = strom.Read(puffer, 0, puffer.Length);
                    if (n > 0 && puffer[0] == 0x16)
                    {
                        Interlocked.Increment(ref tlsVersuche);
                        strom.Write(System.Text.Encoding.ASCII.GetBytes(
                            "HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                        continue;
                    }
                    var text = System.Text.Encoding.ASCII.GetString(puffer, 0, n);
                    // Kopf und Rumpf kommen nicht immer in einem Stueck.
                    for (var mehr = 0; mehr < 5 && text.StartsWith("POST") && !text.Contains("hallo-rumpf"); mehr++)
                    {
                        var m = strom.Read(puffer, 0, puffer.Length);
                        if (m <= 0) { break; }
                        text += System.Text.Encoding.ASCII.GetString(puffer, 0, m);
                    }
                    if (text.Contains("hallo-rumpf")) { inhaltAngekommen = true; }
                    strom.Write(System.Text.Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"));
                    Interlocked.Increment(ref bedient);
                }
            }
            catch (Exception) { }
        }) { IsBackground = true };
        faden.Start();
        try
        {
            using var client = Rivals.ServerHttp.Client(TimeSpan.FromSeconds(10));
            var post = client.PostAsync($"https://127.0.0.1:{port}/api/lap/submit",
                                        new StringContent("hallo-rumpf")).GetAwaiter().GetResult();
            var get = client.GetStringAsync($"https://127.0.0.1:{port}/status").GetAwaiter().GetResult();
            faden.Join(5000);
            if (!post.IsSuccessStatusCode || get != "ok")
            {
                throw new InvalidOperationException(
                    $"Kein Rueckfall auf HTTP: POST {(int)post.StatusCode}, GET '{get}'.");
            }
            if (!inhaltAngekommen)
            {
                throw new InvalidOperationException(
                    "Der Rueckfall schickte die Einreichung ohne ihren Inhalt.");
            }
            if (tlsVersuche != 1 || bedient != 2)
            {
                throw new InvalidOperationException(
                    $"{tlsVersuche} TLS-Versuche und {bedient} HTTP-Antworten -- erwartet 1 und 2: "
                    + "nach dem ersten Scheitern soll gleich HTTP genommen werden.");
            }
        }
        finally { hoerer.Stop(); }

        // KEIN Rueckfall bei einem Fehler, der nichts mit TLS zu tun hat.
        var zu = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        zu.Start();
        var freierPort = ((System.Net.IPEndPoint)zu.LocalEndpoint).Port;
        zu.Stop();
        try
        {
            using var client = Rivals.ServerHttp.Client(TimeSpan.FromSeconds(10));
            client.GetStringAsync($"https://127.0.0.1:{freierPort}/").GetAwaiter().GetResult();
            throw new InvalidOperationException("Ein geschlossener Port lieferte eine Antwort.");
        }
        catch (HttpRequestException e) when (!Rivals.ServerHttp.IstTlsFehler(e)) { }
    }

    private static void CheckDesktopShortcut()
    {
        var ordner = Path.Combine(Path.GetTempPath(),
                                  "forza-lnk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(ordner);
        var ziel = Path.Combine(ordner, "Test.lnk");
        try
        {
            if (!Shortcuts.Create(ShortcutPlace.Desktop, out var fehler, ziel))
            {
                throw new InvalidOperationException(
                    "Die Verknuepfung liess sich nicht anlegen: "
                    + (fehler ?? "ohne Begruendung"));
            }
            if (!File.Exists(ziel))
            {
                throw new InvalidOperationException(
                    "Create meldete Erfolg, aber die Datei ist nicht da.");
            }
            if (new FileInfo(ziel).Length < 100)
            {
                throw new InvalidOperationException(
                    $"Die .lnk ist nur {new FileInfo(ziel).Length} Bytes gross "
                    + "-- da steht kein Ziel drin.");
            }
            // Ein zweiter Aufruf darf nicht scheitern und nichts kaputtmachen.
            if (!Shortcuts.Create(ShortcutPlace.Desktop, out _, ziel))
            {
                throw new InvalidOperationException(
                    "Ein zweiter Aufruf auf dieselbe Datei schlug fehl.");
            }
            // Die Knoepfe im Kopf des Fensters (2026-09-25) lesen das Ziel zurueck:
            // ein Haekchen, das nur "Datei da" prueft, stuende auch neben einer
            // Verknuepfung auf einen laengst geloeschten Ordner.
            if (!Shortcuts.PointsHere(ziel))
            {
                throw new InvalidOperationException(
                    $"Die Verknuepfung zeigt auf '{Shortcuts.TargetOf(ziel)}', "
                    + $"nicht auf diese Kopie ({Shortcuts.ExePath}).");
            }

            // EINE VERALTETE VERKNUEPFUNG: zeigt auf eine Kopie, die es nicht mehr gibt.
            var alt = Path.Combine(ordner, "Alt.lnk");
            var weg = Path.Combine(ordner, "verschoben", AppInfo.ExeName);
            if (!Shortcuts.Create(ShortcutPlace.Desktop, out _, alt, zielExe: weg)
                || Shortcuts.PointsHere(alt))
            {
                throw new InvalidOperationException(
                    "Die veraltete Probe-Verknuepfung liess sich nicht anlegen.");
            }
            // Ohne ersetzen bleibt sie, wie sie ist -- das Zustimmungsfenster
            // ueberschreibt nie etwas, das der Nutzer angelegt haben koennte.
            Shortcuts.Create(ShortcutPlace.Desktop, out _, alt);
            if (Shortcuts.PointsHere(alt))
            {
                throw new InvalidOperationException(
                    "Create ohne ersetzen hat eine vorhandene Verknuepfung ueberschrieben.");
            }
            // Mit ersetzen (der Knopf) zeigt sie danach hierher.
            if (!Shortcuts.Create(ShortcutPlace.Desktop, out var f2, alt, ersetzen: true)
                || !Shortcuts.PointsHere(alt))
            {
                throw new InvalidOperationException(
                    "Der Knopf repariert eine veraltete Verknuepfung nicht: " + (f2 ?? "ohne Grund"));
            }

            // ANGEHEFTET? Gelesen wird der Ordner, in den Windows die Anheftungen legt.
            var anheft = Path.Combine(ordner, "TaskBar");
            if (Shortcuts.IsPinned(anheft))
            {
                throw new InvalidOperationException("Ein fehlender Ordner gilt als angeheftet.");
            }
            Directory.CreateDirectory(anheft);
            Shortcuts.Create(ShortcutPlace.Desktop, out _, Path.Combine(anheft, "Fremd.lnk"), zielExe: weg);
            if (Shortcuts.IsPinned(anheft))
            {
                throw new InvalidOperationException(
                    "Eine Anheftung einer ANDEREN Kopie gilt als diese.");
            }
            File.Copy(ziel, Path.Combine(anheft, AppInfo.Name + ".lnk"));
            if (!Shortcuts.IsPinned(anheft))
            {
                throw new InvalidOperationException(
                    "Eine Anheftung dieser Kopie wird nicht erkannt.");
            }
        }
        finally
        {
            try { Directory.Delete(ordner, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Liest der Bildschirmleser die Laenge unter dem Streckennamen?
    /// </summary>
    /// <remarks>
    /// Nachgestellt ist der Anmeldeschirm vom 2026-09-16: drei Strecken, darunter
    /// je eine Entfernungszeile, die mittlere OHNE Rundenzahl. Geprueft wird nicht
    /// nur, DASS eine Zahl herauskommt, sondern dass jede Strecke die Zeile unter
    /// SICH bekommt -- der nachliegende Fehler waere, jeder Strecke die Laenge
    /// ihres Vorgaengers zu geben, und der faellt bei drei aehnlichen Zahlen nicht
    /// auf.
    /// </remarks>
    private static void CheckRouteLengths()
    {
        var einst = new Rivals.OverlaySettings();
        var datensatz = Rivals.RivalsDataset.FindDefaultPath();
        if (datensatz is null) { return; }
        var rat = new Rivals.RivalsAdvisor(Rivals.RivalsDataset.Load(datensatz));
        var leser = new Rivals.RivalsScreenReader(rat, einst);

        // Name, dann Entfernung, dann Wetter -- wie auf dem Schirm, mit den
        // Y-Abstaenden eines 4K-Bildes.
        var zeilen = new List<Rivals.OcrLine>
        {
            new("Sunflower Scramble", 100, 100),
            new("8.5 KM - 3 LAPS", 100, 160),
            new("Winter / Morning / Cloudy", 100, 220),
            new("Kinkaku-ji Trail", 100, 380),
            new("5.5 KM", 100, 440),
            new("Winter / Morning / Cloudy", 100, 500),
            new("Bamboo Forest Scramble", 100, 660),
            new("15.0 KM - 3 LAPS", 100, 720),
            new("Winter / Early Afternoon / Clear", 100, 780),
        };

        var zustand = leser.Interpret(zeilen, null, Point.Empty);
        void Pruefe(string strecke, double km, int runden)
        {
            if (!zustand.Tracks.Contains(strecke))
            {
                // Die Strecke steht nicht im Datensatz dieses Rechners -- dann
                // prueft dieser Fall nichts, und das ist kein Fehlschlag.
                return;
            }
            if (!zustand.TrackLengths.TryGetValue(strecke, out var l))
            {
                throw new InvalidOperationException(
                    $"Fuer '{strecke}' wurde keine Laenge gelesen.");
            }
            if (Math.Abs(l.TotalKm - km) > 0.01 || l.Laps != runden)
            {
                throw new InvalidOperationException(
                    $"'{strecke}': gelesen {l.TotalKm} km / {l.Laps} Runden, "
                    + $"erwartet {km} / {runden}.");
            }
        }
        Pruefe("Sunflower Scramble", 8.5, 3);
        Pruefe("Kinkaku-ji Trail", 5.5, 1);
        Pruefe("Bamboo Forest Scramble", 15.0, 3);

        CheckHorizonPlayScreen(leser);
        CheckGameLanguages(leser);
    }

    /// <summary>
    /// JEDE SPIELSPRACHE (2026-09-29): ein Spieler mit spanischem Spiel sah weder
    /// Streckenvorschau noch Autowahl. Geprueft wird alles aus config/game_text.json --
    /// den Tabellen des Spiels, nicht selbst uebersetzt: jeder Streckenname fuehrt zu
    /// seiner englischen Strecke, jedes Statuswort wird erkannt, jede Kopfzeile einer
    /// Reihe gibt Rennen und Anzahl her. Dazu ein ganzer spanischer Schirm, wie er in
    /// der Aufnahme eines Spielers stand.
    /// </summary>
    private static void CheckGameLanguages(Rivals.RivalsScreenReader leser)
    {
        if (Rivals.GameText.SprachenAnzahl < 20)
        {
            throw new InvalidOperationException(
                $"config/game_text.json fehlt oder ist unvollstaendig ({Rivals.GameText.SprachenAnzahl} Sprachen) -- "
                + "python scripts/extract_game_text.py");
        }
        var pfad = Rivals.RivalsDataset.FindDefaultPath();
        if (pfad is null) { return; }
        var datensatz = Rivals.RivalsDataset.Load(pfad);
        var rat = new Rivals.RivalsAdvisor(datensatz);
        var falsch = new List<string>();
        var geprueft = 0;
        foreach (var strecke in datensatz.Tracks)
        {
            foreach (var name in Rivals.GameText.Streckennamen(strecke))
            {
                geprueft++;
                var treffer = rat.MatchTrack(name);
                if (treffer?.Track != strecke) { falsch.Add($"'{name}' -> {treffer?.Track ?? "nichts"} statt {strecke}"); }
            }
        }
        if (falsch.Count > 0)
        {
            throw new InvalidOperationException(
                $"{falsch.Count} von {geprueft} Streckennamen anderer Spielsprachen landen falsch: "
                + string.Join("; ", falsch.Take(8)));
        }

        foreach (var (schluessel, englisch, erwartet) in new[]
                 {
                     ("in_progress", "In Progress", Rivals.RouteStatus.InProgress),
                     ("up_next", "Up Next", Rivals.RouteStatus.UpNext),
                 })
        {
            foreach (var wort in Rivals.GameText.Roh(schluessel, englisch))
            {
                var gelesen = Rivals.RivalsScreenReader.StatusIn(wort);
                if (gelesen != erwartet) { falsch.Add($"'{wort}' -> {gelesen}"); }
            }
        }
        foreach (var kopf in Rivals.GameText.Roh("joining", "Joining {0} {1}/{2}"))
        {
            var zeile = string.Format(kopf, "Horizon Play Racing", 2, 3) + " - 20,6 KM";
            var z = leser.Interpret(new List<Rivals.OcrLine> { new(zeile, 100, 40) }, null, Point.Empty);
            if (z.SeriesIndex != 2 || z.SeriesCount != 3 || !z.IsHorizonPlay) { falsch.Add($"'{zeile}' -> {z.Series} {z.SeriesIndex}/{z.SeriesCount}"); }
        }
        if (falsch.Count > 0)
        {
            throw new InvalidOperationException("Worte anderer Spielsprachen nicht erkannt: " + string.Join("; ", falsch.Take(8)));
        }

        // Der spanische Schirm aus der Aufnahme (Y wie im 1440p-Ausschnitt).
        var spanisch = new List<Rivals.OcrLine>
        {
            new("Accediendo a Carreras de Horizon Play 2/3 - 20,6 KM", 100, 40),
            new("Descenso del puente Rainbow", 180, 100),
            new("En curso", 900, 100),
            new("8,5 KM", 180, 140),
            new("Descenso del Norikura", 180, 275),
            new("Siguiente", 900, 275),
            new("5,8 KM", 180, 315),
            new("Carrera de Nachi", 180, 450),
            new("6,3 KM", 180, 490),
        };
        var es = leser.Interpret(spanisch, new List<Rivals.OcrLine> { new("B", 0, 0) }, Point.Empty);
        if (es.Tracks.Count == 3)
        {
            if (!es.Tracks.SequenceEqual(new[] { "Rainbow Bridge Descent", "Norikura Descent", "Nachi Run" })
                || !es.IsOffer || es.Klass != "B" || !es.IsHorizonPlay || es.SeriesIndex != 2 || es.FirstOwnIndex != 1
                || Math.Abs(es.TrackLengths.GetValueOrDefault("Norikura Descent").TotalKm - 5.8) > 0.01)
            {
                throw new InvalidOperationException(
                    $"Spanischer Horizon-Play-Schirm falsch gelesen: {string.Join(" | ", es.Tracks)}, Klasse {es.Klass}, "
                    + $"Reihe {es.Series} {es.SeriesIndex}/{es.SeriesCount}, Einstieg {es.FirstOwnIndex}, "
                    + $"Norikura {es.TrackLengths.GetValueOrDefault("Norikura Descent").TotalKm}");
            }
        }
        Console.WriteLine($"  Spielsprachen: {Rivals.GameText.SprachenAnzahl}, {geprueft} Streckennamen, Statusworte und Kopfzeilen erkannt");
    }

    /// <summary>
    /// Der Anmeldeschirm einer laufenden Horizon-Play-Reihe: Ueberschrift "2/3", die
    /// Statusspalte als eigene Zeile oder an den Namen angehaengt.
    /// </summary>
    private static void CheckHorizonPlayScreen(Rivals.RivalsScreenReader leser)
    {
        // Wie am 2026-09-09 gelesen (Y wie im 1440p-Ausschnitt): "2/3" kam als "213".
        var getrennt = new List<Rivals.OcrLine>
        {
            new("Joining Horizon Play Racing 213 - 25.8 KM", 100, 40),
            new("Sunflower Scramble", 100, 100),
            new("In Progress", 900, 104),
            new("8.5 KM - 3 LAPS", 100, 160),
            new("Kinkaku-ji Trail", 100, 280),
            new("Up Next", 950, 282),
            new("5.5 KM", 100, 340),
            new("Bamboo Forest Scramble", 100, 460),
            new("15.0 KM - 3 LAPS", 100, 520),
        };
        var zustand = leser.Interpret(getrennt, null, Point.Empty);
        if (zustand.Tracks.Count < 3)
        {
            return;   // die Strecken stehen nicht im Datensatz dieses Rechners
        }
        if (zustand.Series != "Horizon Play Racing" || zustand.SeriesIndex != 2 || zustand.SeriesCount != 3)
        {
            throw new InvalidOperationException(
                $"Horizon Play: Ueberschrift falsch gelesen ({zustand.Series} {zustand.SeriesIndex}/{zustand.SeriesCount}).");
        }
        if (zustand.TrackStatus.GetValueOrDefault("Sunflower Scramble") != Rivals.RouteStatus.InProgress
            || zustand.TrackStatus.GetValueOrDefault("Kinkaku-ji Trail") != Rivals.RouteStatus.UpNext
            || zustand.TrackStatus.ContainsKey("Bamboo Forest Scramble"))
        {
            throw new InvalidOperationException(
                "Horizon Play: Statusspalte falsch zugeordnet -- "
                + string.Join(", ", zustand.TrackStatus.Select(p => $"{p.Key}={p.Value}")));
        }
        if (zustand.FirstOwnIndex != 1
            || !zustand.RemainingTracks.SequenceEqual(new[] { "Kinkaku-ji Trail", "Bamboo Forest Scramble" }))
        {
            throw new InvalidOperationException(
                $"Horizon Play: Einstieg {zustand.FirstOwnIndex}, Rest {string.Join(" | ", zustand.RemainingTracks)}.");
        }

        // Angehaengt und verlesen: derselbe Schirm, die Woerter hinter dem Namen.
        var angehaengt = new List<Rivals.OcrLine>
        {
            new("Sunflower Scramble In Pr0gress", 100, 100),
            new("8.5 KM - 3 LAPS", 100, 160),
            new("Kinkaku-ji Trail Up Nexl", 100, 280),
            new("5.5 KM", 100, 340),
            new("Bamboo Forest Scramble", 100, 460),
        };
        var zweiter = leser.Interpret(angehaengt, null, Point.Empty);
        if (zweiter.FirstOwnIndex != 1 || zweiter.SeriesIndex != 0)
        {
            throw new InvalidOperationException(
                $"Horizon Play: angehaengter Status nicht erkannt (Einstieg {zweiter.FirstOwnIndex}).");
        }

        // Der gewoehnliche Anmeldeschirm bleibt, wie er war: alles faehrt man selbst.
        var gewoehnlich = leser.Interpret(getrennt.Where(z => z.X == 100 && z.Y > 50).ToList(), null, Point.Empty);
        if (gewoehnlich.FirstOwnIndex != 0 || gewoehnlich.TrackStatus.Count != 0
            || gewoehnlich.Key.Contains("/ab", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Horizon Play: der gewoehnliche Anmeldeschirm wird fuer eine Reihe gehalten.");
        }
    }

    /// <summary>
    /// Ordnet die Tabelle eine angebotene Strecke dem richtigen eigenen Kurs zu?
    /// </summary>
    /// <remarks>
    /// Drei Kursordner, zwei davon fast gleich lang. Geprueft wird beides:
    ///
    ///   * die EINDEUTIGE Laenge findet ihren Kurs,
    ///   * die MEHRDEUTIGE findet keinen und sagt das auch.
    ///
    /// Der zweite Fall ist der wichtigere. Er ist der Grund, warum diese Tabelle
    /// ueberhaupt neu geschrieben wurde: vorher fuellte sie Spalten mit Zeiten von
    /// Strecken, die gar nicht angeboten waren.
    /// </remarks>
    private static void CheckRouteTable()
    {
        var wurzel = Path.Combine(Path.GetTempPath(),
                                  "forza-routen-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            void Kurs(string name, double meter, double sekunden, int auto)
            {
                var ordner = Path.Combine(wurzel, name, "B", "car" + auto, "stock",
                                          "test");
                Directory.CreateDirectory(ordner);
                // INVARIANT FORMATIEREN. Auf einem deutschen Windows schreibt
                // $"{95.5:0.000}" ein Komma, und der Pfadleser parst ausdruecklich
                // invariant -- die Runde faellt dann lautlos unter den Tisch. Genau
                // so hat dieser Test beim ersten Lauf 0 statt 1 Zeile gemeldet.
                var zahl = sekunden.ToString(
                    "0.000", System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllText(
                    Path.Combine(ordner, $"20260916-120000_{zahl}s.json"), "{}");
                File.WriteAllText(
                    Path.Combine(wurzel, name, "course.json"),
                    "{\"Name\":\"\",\"ShortestMetres\":" + meter.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) + "}");
            }

            Kurs("course_a", 2833, 95.5, 1111);
            Kurs("course_b", 5000, 150.0, 1111);
            Kurs("course_c", 5100, 152.0, 2222);   // fast so lang wie course_b

            var routen = new List<(string Name, double LapMetres)>
            {
                ("Sunflower Scramble", 8500.0 / 3),     // eindeutig -> course_a
                ("Bamboo Forest Scramble", 15000.0 / 3), // course_b UND course_c
            };

            var tabelle = Rivals.OwnCars.Table("B", routen, wurzel);
            if (tabelle.Courses.Count != 2)
            {
                throw new InvalidOperationException(
                    $"Erwartet zwei Spalten, bekommen {tabelle.Courses.Count}.");
            }
            if (tabelle.Courses[0].Key != "course_a")
            {
                throw new InvalidOperationException(
                    "Die eindeutige Laenge fand ihren Kurs nicht: "
                    + $"'{tabelle.Courses[0].Key}'.");
            }
            if (tabelle.Courses[1].Key.Length != 0 || !tabelle.Courses[1].Ambiguous)
            {
                throw new InvalidOperationException(
                    "Zwei gleich lange Kurse haetten KEINE Spalte fuellen duerfen, "
                    + $"es wurde '{tabelle.Courses[1].Key}' gewaehlt.");
            }
            if (tabelle.Rows.Count != 1
                || !tabelle.Rows[0].ByCourse.ContainsKey("course_a"))
            {
                throw new InvalidOperationException(
                    "Erwartet genau ein Auto mit einer Zeit auf course_a, "
                    + $"bekommen {tabelle.Rows.Count} Zeile(n).");
            }

            // Und mit einem NAMEN im Kursvermerk muss die Mehrdeutigkeit weichen.
            File.WriteAllText(Path.Combine(wurzel, "course_c", "course.json"),
                              "{\"Name\":\"Bamboo Forest Scramble\","
                              + "\"ShortestMetres\":5100}");
            Directory.SetLastWriteTimeUtc(wurzel, DateTime.UtcNow.AddSeconds(5));
            var zweite = Rivals.OwnCars.Table("B", routen, wurzel);
            if (zweite.Courses[1].Key != "course_c")
            {
                throw new InvalidOperationException(
                    "Ein gesetzter Streckenname muss die Laenge schlagen, "
                    + $"gewaehlt wurde '{zweite.Courses[1].Key}'.");
            }
        }
        finally
        {
            try { Directory.Delete(wurzel, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Notizen zu Autos: finden, behalten, unterscheiden.
    /// </summary>
    /// <remarks>
    /// Drei Dinge werden geprueft, und das dritte ist das eigentliche:
    ///
    ///   1. Was geschrieben wurde, kommt zurueck -- auch nach neuem Laden.
    ///   2. Eine ANDERE Abstimmung ist ein anderer Schluessel. Sonst zeigte die
    ///      Notiz zum Rennaufbau am Serienauto, und das waere schlimmer als keine.
    ///   3. Die Leistung gehoert NICHT in den Schluessel. Sie ist ein Momentanwert;
    ///      haenge sie im Schluessel, waere dieselbe Notiz nach einer schnelleren
    ///      Runde verschwunden.
    /// </remarks>
    private static void CheckCarNotes()
    {
        var datei = Path.Combine(Path.GetTempPath(),
                                 "forza-notes-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var notes = new Rivals.CarNotes(datei);
            var serie = Rivals.CarNotes.Fingerprint(1276, 600, 1, 4, 7000, 900);
            var renn = Rivals.CarNotes.Fingerprint(1276, 798, 1, 4, 7000, 900);

            if (serie == renn)
            {
                throw new InvalidOperationException(
                    "Zwei Abstimmungen desselben Autos ergeben denselben Schluessel.");
            }

            notes.Note(serie, 1276, "Subaru BRZ '13", 600, 150);
            notes.SetComment(serie, "untersteuert ab Kurve 3");
            notes.Note(renn, 1276, "Subaru BRZ '13", 798, 300);

            // Die Leistung darf den Schluessel nicht aendern.
            var nachher = Rivals.CarNotes.Fingerprint(1276, 600, 1, 4, 7000, 900);
            if (nachher != serie)
            {
                throw new InvalidOperationException(
                    "Der Schluessel haengt an etwas, das sich aendert.");
            }

            // Neu laden: steht die Notiz noch da?
            var wieder = new Rivals.CarNotes(datei);
            var e = wieder.Lookup(serie);
            if (e is null || e.Comment != "untersteuert ab Kurve 3")
            {
                throw new InvalidOperationException(
                    $"Die Notiz ueberlebt das Speichern nicht: {e?.Comment ?? "(nichts)"}");
            }
            if (!string.IsNullOrWhiteSpace(wieder.Lookup(renn)?.Comment))
            {
                throw new InvalidOperationException(
                    "Die Notiz der einen Abstimmung steht an der anderen.");
            }
            // Aber ueber das AUTO gefragt, gibt es sie.
            if (wieder.ByOrdinal(1276)?.Comment != "untersteuert ab Kurve 3")
            {
                throw new InvalidOperationException(
                    "Die Notiz ist ueber die Auto-Kennung nicht zu finden.");
            }
            // PS aus kW, zur Auskunft.
            if (wieder.Lookup(renn) is { } r && r.HorsePower < 380)
            {
                throw new InvalidOperationException(
                    $"300 kW sollten rund 402 PS sein, gerechnet wurden {r.HorsePower}.");
            }
        }
        finally
        {
            try { File.Delete(datei); } catch (Exception) { }
        }
    }

    private static void CheckCourseIdentity()
    {
        var wurzel = Path.Combine(Path.GetTempPath(),
                                  "forza-course-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // Gleicher Start, WEIT auseinanderliegende Ziele.
            Ablegen(wurzel, ordinal: 7, pi: 600, sekunden: 80f, x: 500f, z: 500f,
                    zielX: 3000f, zielZ: 3000f);
            Ablegen(wurzel, ordinal: 7, pi: 600, sekunden: 40f, x: 505f, z: 495f,
                    zielX: -2000f, zielZ: 1000f);

            var ordner = Rivals.LapArchive.KursOrdner(wurzel).ToArray();
            if (ordner.Length != 2)
            {
                throw new InvalidOperationException(
                    $"Gleiche Startlinie, verschiedene Ziele: erwartet 2 Ordner, "
                    + $"bekommen {ordner.Length} ({string.Join(", ", ordner.Select(Path.GetFileName))}).");
            }

            // Und umgekehrt: dieselbe Strecke ein zweites Mal gefahren, ein paar
            // Meter daneben, muss im SELBEN Ordner landen.
            Ablegen(wurzel, ordinal: 8, pi: 600, sekunden: 82f, x: 510f, z: 490f,
                    zielX: 3040f, zielZ: 2960f);
            ordner = Rivals.LapArchive.KursOrdner(wurzel).ToArray();
            if (ordner.Length != 2)
            {
                throw new InvalidOperationException(
                    "Dieselbe Strecke ein zweites Mal gefahren hat einen dritten "
                    + $"Ordner angelegt ({ordner.Length} insgesamt).");
            }
        }
        finally
        {
            try { Directory.Delete(wurzel, recursive: true); } catch (Exception) { }
        }
    }

    private static void CheckOwnCars()
    {
        var wurzel = Path.Combine(Path.GetTempPath(),
                                  "forza-owncars-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // Zwei Autos auf DEMSELBEN Kurs, eines davon schneller, plus ein
            // drittes woanders. Erwartet: ein Direktvergleich, drei Autos.
            Ablegen(wurzel, ordinal: 11, pi: 600, sekunden: 90.5f, x: 100f, z: 200f);
            Ablegen(wurzel, ordinal: 22, pi: 600, sekunden: 95.25f, x: 100f, z: 200f);
            Ablegen(wurzel, ordinal: 33, pi: 600, sekunden: 60.0f, x: 900f, z: 900f);
            // Eine langsamere Runde desselben Autos darf die Bestzeit nicht kippen.
            Ablegen(wurzel, ordinal: 11, pi: 600, sekunden: 99.0f, x: 100f, z: 200f);

            var besten = Rivals.OwnCars.Bests("B", wurzel);
            if (besten.Count != 3)
            {
                throw new InvalidOperationException(
                    $"Erwartet 3 Autos in B, bekommen {besten.Count}.");
            }
            var elf = besten.First(b => b.Ordinal == 11);
            if (Math.Abs(elf.BestSeconds - 90.5) > 0.01)
            {
                throw new InvalidOperationException(
                    $"Bestzeit von Auto 11 ist {elf.BestSeconds}, erwartet 90.5 "
                    + "-- die langsamere Runde hat sie verdraengt.");
            }
            if (elf.Laps != 2)
            {
                throw new InvalidOperationException(
                    $"Auto 11 hat {elf.Laps} Runde(n), erwartet 2.");
            }

            var duelle = Rivals.OwnCars.Duels("B", wurzel);
            if (duelle.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Erwartet genau einen Direktvergleich, bekommen {duelle.Count}.");
            }
            if (duelle[0].Order[0].Ordinal != 11)
            {
                throw new InvalidOperationException(
                    "Im Direktvergleich steht nicht das schnellere Auto vorn.");
            }

            // Eine andere Klasse darf nichts davon sehen.
            if (Rivals.OwnCars.Bests("S1", wurzel).Count != 0)
            {
                throw new InvalidOperationException(
                    "Die Klassengrenze haelt nicht -- S1 sieht Runden aus B.");
            }

            if (Rivals.OwnCars.TimeText(68.09) != "1:08.09"
                || Rivals.OwnCars.TimeText(45.5) != "45.50s")
            {
                throw new InvalidOperationException(
                    "Die Zeitdarstellung stimmt nicht: "
                    + Rivals.OwnCars.TimeText(68.09) + " / "
                    + Rivals.OwnCars.TimeText(45.5));
            }

        }
        finally
        {
            try { Directory.Delete(wurzel, recursive: true); } catch (Exception) { }
        }
    }

    /// <summary>Eine Runde wirklich ablegen, damit der Pfad echt ist.</summary>
    private static void Ablegen(string wurzel, int ordinal, int pi, float sekunden,
                                float x, float z,
                                float? zielX = null, float? zielZ = null)
    {
        // Der Zielpunkt wandert vom Start zum Ziel. Ohne Angabe endet die Fahrt
        // dicht beim Start -- das ist die Rundenfahrt; mit Angabe der Sprint.
        var zx = zielX ?? (x + 11f);
        var zz = zielZ ?? (z + 11f);
        var punkte = new List<Rivals.LapSample>();
        for (var i = 0; i < 12; i++)
        {
            var anteil = i / 11f;
            punkte.Add(new Rivals.LapSample
            {
                Seconds = sekunden * anteil,
                Metres = 1500f * anteil,
                X = x + ((zx - x) * anteil),
                Z = z + ((zz - z) * anteil),
            });
        }
        var runde = new Rivals.RecordedLap
        {
            LapSeconds = sekunden, LengthMetres = 1500f,
            CarOrdinal = ordinal, PerformanceIndex = pi, CarClass = 2,
            Drivetrain = 1, Cylinders = 6, MaxRpm = 7000, IdleRpm = 800,
            StartX = x, StartZ = z,
            // Verschiedene Zeitpunkte, sonst ueberschreibt die zweite Runde
            // desselben Autos die erste -- der Dateiname traegt die Sekunde.
            RecordedAt = DateTimeOffset.Now.AddSeconds(-ordinal - (int)sekunden),
            Samples = punkte,
        };
        if (Rivals.LapArchive.Save(runde, null, wurzel) is null)
        {
            throw new InvalidOperationException(
                $"Die Testrunde (Auto {ordinal}) liess sich nicht ablegen.");
        }
    }

    private static void CheckPanelScrolling()
    {
        var lines = new List<Rivals.PanelLine>
        {
            new("by points \u00b7 3 route(s)", string.Empty,
                Rivals.OverlayPanel.Bar, Heading: true),
        };
        const int cars = 474;
        for (var at = 1; at <= cars; at++)
        {
            lines.Add(new Rivals.PanelLine($"{at,3}. Car {at}", $"{1000 - at} pts",
                                           Rivals.OverlayPanel.Ink));
        }

        using var panel = new Rivals.OverlayPanel(new Rectangle(0, 0, 520, 900), 0.9);
        panel.SetContent("Class S1 \u00b7 Cross-Country \u2013 what to drive",
                         "three routes", lines, "a note");
        using var canvas = new Bitmap(panel.Width, panel.Height);
        void Paint() => panel.DrawToBitmap(canvas, new Rectangle(0, 0, panel.Width,
                                                                 panel.Height));
        Paint();

        var pages = 0;
        while (panel.ScrollPage(1))
        {
            Paint();
            if (++pages > cars)
            {
                throw new InvalidOperationException(
                    "Paging down a panel never reached the end of the list.");
            }
        }
        if (pages < 2)
        {
            throw new InvalidOperationException(
                $"A {cars}-car ranking paged down only {pages} time(s); the panel is "
                + "still showing the whole list at once.");
        }
        // Ten rows a page at the very least. Without this the check passes even when
        // nothing is painted at all: a panel that believes it holds one row still
        // walks the list, one car at a time, and calls it scrolling.
        if (pages > cars / 10)
        {
            throw new InvalidOperationException(
                $"{cars} cars took {pages} pages -- fewer than ten rows a page, so the "
                + "panel is not measuring what it draws.");
        }
        var back = 0;
        while (panel.ScrollPage(-1))
        {
            Paint();
            if (++back > cars)
            {
                throw new InvalidOperationException(
                    "Paging up a panel never reached the top of the list.");
            }
        }
        if (back != pages)
        {
            throw new InvalidOperationException(
                $"Paged down {pages} times but back up {back}; the walk is not "
                + "reversible.");
        }
        // And a fresh panel of the same shape starts at the top, not where the last
        // one was left.
        panel.SetContent("Class A \u2013 what to drive", "other routes", lines, "a note");
        Paint();
        if (panel.ScrollPage(-1))
        {
            throw new InvalidOperationException(
                "New content did not rewind the panel to the top.");
        }
    }

    private static void CheckGameWatch()
    {
        var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
        if (!new GameWatch(self).Running)
        {
            throw new InvalidOperationException(
                $"GameWatch does not find the running process it is looking at ({self}).");
        }
        // Auch mit ".exe" geschrieben muss es gehen: so steht es in Aufgabenplanern,
        // in Anleitungen und darum irgendwann in config/overlay.json.
        if (!new GameWatch(self + ".exe").Running)
        {
            throw new InvalidOperationException(
                "GameWatch fails when the configured name carries the .exe suffix.");
        }

        // Die Gegenprobe geht nur, wenn nicht wirklich ein Forza laeuft -- die
        // Ersatzsuche nach "forza" UND "horizon" wuerde dann richtig anschlagen.
        var forzaLike = System.Diagnostics.Process.GetProcesses().Any(process =>
            process.ProcessName.Contains("forza", StringComparison.OrdinalIgnoreCase)
            && process.ProcessName.Contains("horizon", StringComparison.OrdinalIgnoreCase));
        if (!forzaLike && new GameWatch("kein-solcher-prozess-4711").Running)
        {
            throw new InvalidOperationException(
                "GameWatch reports a process that does not exist -- both guards would "
                + "be dead weight.");
        }
    }

    /// <summary>
    /// Zwei Zahlen muessen uebereinstimmen -- mit angebbarer Genauigkeit.
    /// </summary>
    /// <remarks>
    /// Die Vorgabe von einem Millimeter passt fuer gerechnete Groessen. Fuer
    /// GEMESSENE braucht es mehr Luft: ein Kreis, in 2-m-Sehnen abgefahren, ergibt
    /// 1999,988 statt 2000 m -- die Sehne ist kuerzer als der Bogen. Eine Pruefung,
    /// die daran scheitert, prueft die Diskretisierung und nicht die Aufzeichnung.
    /// </remarks>
    /// <summary>
    /// Die Erkennung einer Ueberfahrt -- reine Geometrie, ohne Spiel.
    /// </summary>
    /// <remarks>
    /// Drei Fragen, und jede hat schon einmal eine falsche Antwort bekommen:
    /// Zaehlt eine Vorbeifahrt dicht an der Linie? Zaehlt eine WEIT daneben nicht?
    /// Und merkt sich die Erkennung, in welche RICHTUNG gefahren wurde -- denn wer
    /// dieselbe Linie rueckwaerts ueberquert, hat gewendet und keine Runde gedreht.
    /// </remarks>
    private static void CheckFreeRoamLines()
    {
        static Rivals.FreeRoamTimer MitLinie()
        {
            var t = new Rivals.FreeRoamTimer();
            t.Add(new Rivals.FreeRoamAnchor { Name = "Pruefung", X = 0f, Z = 0f });
            return t;
        }

        // Dicht vorbei: 10 m seitlich, gerade Fahrt entlang X.
        var nah = MitLinie();
        var treffer = new List<Rivals.FreeRoamTimer.Pass>();
        for (var x = -300f; x <= 300f; x += 2f)
        {
            var p = nah.Step(x, 10f, (x + 300f) / 20f);
            if (p is not null) { treffer.Add(p.Value); }
        }
        if (treffer.Count != 1)
        {
            throw new InvalidOperationException(
                $"Eine Vorbeifahrt 10 m neben der Linie ergab {treffer.Count} "
                + "Ueberfahrten statt einer.");
        }
        // Die groesste Annaeherung liegt bei x = 0, also nach 300 m, also bei 15 s.
        AssertNear(treffer[0].Seconds, 15.0, "Zeitpunkt der Ueberfahrt", 0.15);
        AssertNear(treffer[0].Distance, 10.0, "Abstand der Ueberfahrt", 0.5);
        AssertNear(treffer[0].HeadingX, 1.0, "Fahrtrichtung bei der Ueberfahrt", 0.01);

        // Weit vorbei: 90 m seitlich. In der Beobachtungszone (120 m), aber keine
        // Ueberfahrt (40 m). Ohne diese Unterscheidung wuerde jede Parallelstrasse
        // die Uhr starten.
        var weit = MitLinie();
        var weitTreffer = 0;
        for (var x = -300f; x <= 300f; x += 2f)
        {
            if (weit.Step(x, 90f, (x + 300f) / 20f) is not null) { weitTreffer++; }
        }
        if (weitTreffer != 0)
        {
            throw new InvalidOperationException(
                $"Eine Fahrt 90 m neben der Linie zaehlte als {weitTreffer} "
                + "Ueberfahrt(en) -- eine Parallelstrasse wuerde die Uhr starten.");
        }

        // Hin und zurueck: zwei Ueberfahrten, entgegengesetzte Richtungen.
        var hinUndZurueck = MitLinie();
        var beide = new List<Rivals.FreeRoamTimer.Pass>();
        for (var x = -300f; x <= 300f; x += 2f)
        {
            var p = hinUndZurueck.Step(x, 5f, (x + 300f) / 20f);
            if (p is not null) { beide.Add(p.Value); }
        }
        for (var x = 300f; x >= -300f; x -= 2f)
        {
            var p = hinUndZurueck.Step(x, 5f, (930f - x) / 20f);
            if (p is not null) { beide.Add(p.Value); }
        }
        if (beide.Count != 2)
        {
            throw new InvalidOperationException(
                $"Hin und zurueck ergab {beide.Count} Ueberfahrten statt zwei.");
        }
        var skalar = beide[0].HeadingX * beide[1].HeadingX
                     + beide[0].HeadingZ * beide[1].HeadingZ;
        if (skalar >= 0f)
        {
            throw new InvalidOperationException(
                $"Hin- und Rueckfahrt gelten als dieselbe Richtung (Skalarprodukt "
                + $"{skalar:0.00}) -- ein Wenden wuerde als Runde zaehlen.");
        }
    }

    /// <summary>
    /// Time Attack in der freien Welt: eine ganze Runde, selbst gestoppt.
    /// </summary>
    /// <remarks>
    /// DER FALL, DEN ES GIBT: in Horizon steht `IsRaceOn` auch im freien Fahren auf
    /// 1, `CurrentLap` aber auf 0 -- gemessen an 18.676 Paketen. Bis zum 2026-09-14
    /// hiess das: im freien Fahren wurde NIE etwas aufgezeichnet, und der Streifen
    /// zeigte "free roam -- no timed run".
    ///
    /// Hier wird ein Rundkurs von 2.000 m viermal gefahren, mit stehender Spieluhr.
    /// Erwartet werden drei vollstaendige Runden: die erste Ueberfahrt liegt noch
    /// vor der Erkennung des freien Fahrens (die braucht 150 m stehende Uhr), die
    /// zweite startet die eigene Uhr, und ab der dritten wird abgeschlossen.
    /// </remarks>
    private static void CheckFreeRoamTimeAttack()
    {
        const float umfang = 2000f;
        const float tempo = 20f;           // m/s
        var radius = umfang / (2f * MathF.PI);

        var aufnahme = new Rivals.LapRecorder();
        var linien = new Rivals.FreeRoamTimer();
        // Die Linie liegt beim Start des Kreises: (radius, 0).
        linien.Add(new Rivals.FreeRoamAnchor
        {
            Name = "Pruefkreis", X = radius, Z = 0f, Source = "manual",
        });
        aufnahme.Lines = linien;

        var fertige = new List<Rivals.RecordedLap>();
        aufnahme.LapCompleted += (_, lap) => fertige.Add(lap);

        for (var m = 0f; m <= 4f * umfang + 100f; m += 2f)
        {
            var winkel = m / radius;
            // Die Spieluhr steht auf 0 -- das ist der ganze Punkt.
            if (!ForzaPacket.TryParse(LapPacket(
                    1, 0, m, 0f, 0f, 4321, 750, 5,
                    x: radius * MathF.Cos(winkel), z: radius * MathF.Sin(winkel),
                    timestampMs: (uint)(m / tempo * 1000f), speed: tempo),
                out var paket))
            {
                throw new InvalidOperationException("Das Freifahrtpaket parste nicht.");
            }
            aufnahme.OnTelemetry(paket);
        }

        if (!aufnahme.InFreeRoam)
        {
            throw new InvalidOperationException(
                "Eine Fahrt mit stehender Spieluhr wurde nicht als freies Fahren "
                + "erkannt -- dann laeuft dort nie eine Uhr.");
        }
        if (fertige.Count != 3)
        {
            throw new InvalidOperationException(
                $"Vier Runden im freien Fahren ergaben {fertige.Count} "
                + "Aufzeichnungen statt drei.");
        }
        foreach (var runde in fertige)
        {
            if (!runde.FreeRoam)
            {
                throw new InvalidOperationException(
                    "Eine selbst gestoppte Runde ist nicht als solche vermerkt -- "
                    + "sie wuerde als gewertete Bestzeit gelten.");
            }
            // 2.000 m bei 20 m/s sind 100 s.
            AssertNear(runde.LapSeconds, 100.0, "Dauer der freien Runde", 0.3);
            // Exakt, nicht ungefaehr: der Wegzaehler laeuft von Linie zu Linie.
            AssertNear(runde.LengthMetres, 2000.0, "Laenge der freien Runde", 2.5);
            if (runde.CarOrdinal != 4321 || runde.PerformanceIndex != 750)
            {
                throw new InvalidOperationException(
                    $"Die Fahrzeugdaten fehlen: Auto {runde.CarOrdinal}, "
                    + $"PI {runde.PerformanceIndex}. Ohne sie ist die Runde im "
                    + "Bestand nicht einzuordnen -- und genau danach war gefragt.");
            }
            // Der Startpunkt ist die LINIE, nicht der Ort des Autos: nur so landen
            // alle Fahrten ueber dieselbe Linie im selben Streckenordner.
            AssertNear(runde.StartX, radius, "Startpunkt der freien Runde", 0.01);
        }

        // UND DER RUECKWEG: faengt die Spieluhr an zu laufen, muss die Aufzeichnung
        // sofort wieder greifen. Ohne die Nullstellung von _lapNumber bliebe der
        // Rekorder bis zum naechsten Rundenwechsel stumm -- in einem Sprint also
        // fuer immer.
        var stempel = (uint)((4f * umfang + 100f) / tempo * 1000f);
        for (var m = 0f; m <= 2000f; m += 2f)
        {
            stempel += 100u;
            if (ForzaPacket.TryParse(LapPacket(
                    1, 0, m, m / tempo, 0f, 4321, 750, 5,
                    x: 90000f + m, z: 90000f,
                    timestampMs: stempel, speed: tempo), out var paket))
            {
                aufnahme.OnTelemetry(paket);
            }
        }
        if (aufnahme.InFreeRoam)
        {
            throw new InvalidOperationException(
                "Die laufende Spieluhr beendete das freie Fahren nicht.");
        }
        if (!aufnahme.HasLapUnderway || aufnahme.CurrentMetres < 1900f)
        {
            throw new InvalidOperationException(
                $"Nach der Rueckkehr ins Rennen zeichnet der Rekorder nicht auf "
                + $"({aufnahme.CurrentMetres:0} m von 2.000). Ein Sprint waere damit "
                + "vollstaendig verloren.");
        }
    }

    /// <summary>
    /// Der Tuning-Inspektor, an einer selbst gebauten Garage.
    /// </summary>
    /// <remarks>
    /// OHNE SPIEL UND OHNE SPEICHERABZUG. Der Leseweg endet bei einer SQLite-Datei;
    /// ob die aus dem Arbeitsspeicher von Forza stammt oder hier entsteht, ist ihm
    /// gleich. Damit laesst sich genau das pruefen, was sonst nur mit laufendem Spiel
    /// zu sehen waere -- und ohne dass die Pruefung vom privaten Bestand des Nutzers
    /// abhaengt.
    ///
    /// Geprueft wird, was schon einmal falsch war:
    /// - die Klasse kommt aus `ClassID` und nicht aus einer PI-Rechnung,
    /// - Stufe 0 heisst Serie,
    /// - eine fremde Teilefamilie wird als solche erkannt (Motortausch),
    /// - ein Sammelkauf wird EINMAL berechnet und nicht siebenmal,
    /// - ein Regler mit -1 ist "nicht vorhanden" und nicht "Anschlag links".
    /// </remarks>
    private static void CheckTuningInspector()
    {
        var ordner = Path.Combine(AppInfo.TempFolder, "selftest");
        Directory.CreateDirectory(ordner);
        var datei = Path.Combine(ordner, "garage_probe.db");
        if (File.Exists(datei)) { File.Delete(datei); }

        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(
                   "Data Source=" + datei))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                create table Career_Garage (
                    Id integer, CarId integer, PerformanceIndex real, ClassID integer,
                    PartsValue integer, CurbWeight real, TopSpeed real,
                    SimPeakPower real, OriginalOwner text, TuneFileName text,
                    VersionedTuneId integer, VersionedTuneXUID integer, SharedID integer,
                    Engine integer, Camshaft integer, Clutch integer, CarBody integer,
                    FrontBumper integer, Hood integer, RearWing integer,
                    Tuning_frontTirePressure real, Tuning_eighthGear real,
                    Tuning_rearSwaybar real);
                create table Career_PurchasedParts (
                    GarageId integer, UngroupedPartEnum integer, PartId integer,
                    PricePaid integer);
                insert into Career_Garage values (
                    7, 1234, 0.6058, 3, 99000, 12.0, 80.0, 5000.0, 'Fahrer',
                    'Tuning_1234', 42, 999, 5,
                    1234000, 9999003, 2102004, 1234100, 1234100, 1234100, -1,
                    0.275, -1.0, 1.0);
                insert into Career_PurchasedParts values (7, 1, 9999003, 2100);
                insert into Career_PurchasedParts values (7, 2, 2102004, 3000);
                insert into Career_PurchasedParts values (7, 3, 1234100, 20000);";
            cmd.ExecuteNonQuery();
        }

        var gefunden = Tuning.GarageReader.FindGarage(new[] { datei });
        if (gefunden is null)
        {
            throw new InvalidOperationException(
                "Die selbst gebaute Garage wurde nicht als solche erkannt.");
        }

        var tune = Tuning.GarageReader.Read(gefunden, 1234)
                   ?? throw new InvalidOperationException("Auto 1234 nicht gelesen.");

        // 1) Die Klasse kommt aus ClassID. 3 ist A -- und NICHT das, was ein PI von
        //    605,8 nahelegen wuerde (das waere B).
        if (tune.ClassName != "A")
        {
            throw new InvalidOperationException(
                $"ClassID 3 wurde als '{tune.ClassName}' gelesen statt als 'A'. "
                + "Die Klasse darf nicht aus PerformanceIndex gerechnet werden: das "
                + "widerspricht bei 128 von 569 Autos der Angabe des Spiels.");
        }

        Tuning.TunePart Teil(string name) =>
            tune.Parts.FirstOrDefault(p => p.Label == name)
            ?? throw new InvalidOperationException($"Teil '{name}' fehlt im Bericht.");

        // 2) Stufe 0 ist Serie, und die eigene Familie ist nicht fremd.
        var motor = Teil("Engine block");
        if (motor.Step != 0 || motor.Origin != "own")
        {
            throw new InvalidOperationException(
                $"Der Serienmotor kam als Stufe {motor.Step}, Herkunft "
                + $"'{motor.Origin}' -- erwartet Stufe 0 und 'own'.");
        }

        // 3) Eine fremde Familie ist ein Motortausch und muss auffallen.
        // Ohne Namensaufloeser ist eine fremde Familie ein UNIVERSALTEIL -- erst wenn
        // sich die Nummer als Auto entpuppt, ist es ein Motortausch. Beides wird
        // geprueft, weil genau diese Unterscheidung der Nutzer eingefordert hat.
        var nocken = Teil("Camshaft");
        if (nocken.Origin != "universal" || nocken.Family != 9999 || nocken.Step != 3)
        {
            throw new InvalidOperationException(
                $"Die fremde Nockenwelle kam als Familie {nocken.Family}, Herkunft "
                + $"'{nocken.Origin}', Stufe {nocken.Step}.");
        }
        var mitNamen = Tuning.GarageReader.Read(
            gefunden, 1234, nummer => nummer == 9999 ? "Testwagen '99" : null)
            ?? throw new InvalidOperationException("Auto 1234 nicht gelesen.");
        var getauscht = mitNamen.Parts.First(p => p.Label == "Camshaft");
        if (getauscht.Origin != "swap" || getauscht.FamilyName != "Testwagen '99")
        {
            throw new InvalidOperationException(
                $"Mit Namensaufloeser kam Herkunft '{getauscht.Origin}', Name "
                + $"'{getauscht.FamilyName}' -- erwartet 'swap' und der Autoname.");
        }
        if (!getauscht.Erklaerung.Contains("Testwagen"))
        {
            throw new InvalidOperationException(
                $"Die Erklaerung nennt das Auto nicht: '{getauscht.Erklaerung}'. "
                + "Genau daran ist die erste Fassung gescheitert -- 'foreign family "
                + "3899' sagt niemandem etwas.");
        }

        // 4) EIN Sammelkauf, nicht drei. Karosserie, Stossstange und Haube tragen
        //    dieselbe Nummer; 20.000 CR duerfen nur einmal dastehen.
        var mitPreis = new[] { "Car body", "Front bumper", "Hood" }
            .Count(n => Teil(n).PricePaid is not null);
        if (mitPreis != 1)
        {
            throw new InvalidOperationException(
                $"Ein Sammelkauf wurde {mitPreis}-mal berechnet statt einmal -- so "
                + "sehen 20.000 CR wie 60.000 aus.");
        }

        // 5) Ein nicht verbautes Teil taucht gar nicht erst auf.
        if (tune.Parts.Any(p => p.Label == "Rear wing"))
        {
            throw new InvalidOperationException(
                "Ein Teil mit Wert -1 wurde als verbaut gemeldet.");
        }

        // 6) Ein Regler mit -1 gibt es an diesem Auto nicht -- er darf nicht als
        //    Anschlag links durchgehen.
        var achter = tune.Settings.FirstOrDefault(s => s.Label == "8th gear")
            ?? throw new InvalidOperationException("Der achte Gang fehlt in der Liste.");
        if (achter.Slider >= 0)
        {
            throw new InvalidOperationException(
                $"Der nicht vorhandene achte Gang kam als {achter.Slider:0.000}.");
        }
        var stabi = tune.Settings.FirstOrDefault(s => s.Label == "Anti-roll bar, rear")
            ?? throw new InvalidOperationException("Der hintere Stabilisator fehlt.");
        AssertNear(stabi.Slider, 1.0, "Reglerposition hinterer Stabilisator", 0.0001);

        // 7) Die Herkunft des Tunes gehoert dazu -- der Fahrer ist nicht der Tuner.
        if (tune.TuneFileName != "Tuning_1234" || tune.VersionedTuneXuid != 999)
        {
            throw new InvalidOperationException(
                "Die Herkunft des Tunes wurde nicht gelesen.");
        }

        try { File.Delete(datei); } catch (Exception) { }
    }

    /// <summary>
    /// Der Speicher-Scanner, am EIGENEN Prozess bewiesen.
    /// </summary>
    /// <remarks>
    /// ## Warum diese Pruefung sein muss
    ///
    /// `ForzaMemoryDb` ist eine Uebertragung des Python-Werkzeugs nach C#, und sie
    /// laesst sich normalerweise nur mit laufendem Forza ausprobieren. Genau daraus
    /// entsteht die Sorte Fehler, die dieses Projekt schon zweimal teuer bezahlt hat:
    /// gebaut, gebaut geglaubt, nie am Ziel gelaufen.
    ///
    /// Der Scanner braucht Forza aber gar nicht -- er braucht einen Prozess, in
    /// dessen Speicher ein SQLite-Abbild liegt. So einen kann diese Pruefung selbst
    /// herstellen: Datenbank anlegen, ihre Bytes in ein Feld laden, den eigenen
    /// Prozess absuchen. Damit sind Regionsdurchlauf, Kopfpruefung, Lesen und
    /// Schreiben belegt; offen bleibt allein, ob Forzas Regionen sich anders
    /// verhalten -- und das kann nur ein Lauf am Spiel zeigen.
    /// </remarks>
    private static void CheckMemoryScanner()
    {
        var ordner = Path.Combine(AppInfo.TempFolder, "scanprobe");
        if (Directory.Exists(ordner))
        {
            try { Directory.Delete(ordner, true); } catch (Exception) { }
        }
        Directory.CreateDirectory(ordner);
        var quelle = Path.Combine(ordner, "vorlage.db");

        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + quelle))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = @"
                create table Career_Garage (
                    Id integer, CarId integer, PerformanceIndex real, ClassID integer,
                    PartsValue integer, Engine integer,
                    Tuning_frontTirePressure real);
                insert into Career_Garage values (1, 4321, 0.5, 2, 0, 4321000, 0.5);";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // DIE BYTES IN DEN EIGENEN SPEICHER. Genau das tut Forza mit seinen
        // Datenbanken auch -- ein ganzes Dateiabbild im Heap.
        var abbild = File.ReadAllBytes(quelle);
        if (abbild.Length < 512)
        {
            throw new InvalidOperationException("Die Probedatenbank ist zu klein.");
        }

        var gefunden = Tuning.ForzaMemoryDb.DumpFrom(
            Environment.ProcessId, Path.Combine(ordner, "abzug"));

        // Das Feld muss bis hierher am Leben bleiben, sonst raeumt der Sammler es
        // weg, bevor gesucht wird.
        GC.KeepAlive(abbild);

        if (gefunden.Count == 0)
        {
            throw new InvalidOperationException(
                "Der Speicher-Scanner fand im eigenen Prozess keine einzige "
                + "SQLite-Datenbank, obwohl eine dort liegt. Dann faende er auch in "
                + "Forza keine.");
        }

        var garage = Tuning.GarageReader.FindGarage(gefunden.Select(f => f.Path));
        if (garage is null)
        {
            throw new InvalidOperationException(
                $"{gefunden.Count} Abbild(er) herausgeholt, aber keines liess sich als "
                + "Garage oeffnen -- der Kopf wird also falsch gelesen oder es wird "
                + "die falsche Menge kopiert.");
        }

        var autos = Tuning.GarageReader.Cars(garage);
        if (!autos.Contains(4321))
        {
            throw new InvalidOperationException(
                "Die aus dem Speicher geholte Garage kennt das eingetragene Auto "
                + "nicht -- es wurde etwas anderes kopiert als gemeint.");
        }

        try { Directory.Delete(ordner, true); } catch (Exception) { }
    }

    /// <summary>
    /// Der Teilename zur Ausbaustufe.
    /// </summary>
    /// <remarks>
    /// ## Warum diese Pruefung existiert
    ///
    /// Weil der Nachschlageweg schon einmal stumm zurueckgefallen ist. In
    /// `PartNames.Zahl` stand ein regulaerer Ausdruck, in den beim Schreiben der
    /// Datei zwei echte RUECKSCHRITT-ZEICHEN geraten waren (0x08) -- aus einem ``
    /// fuer die Wortgrenze war ein Steuerzeichen geworden. Das Muster suchte also
    /// nach einem Backspace vor der Ziffer und traf nie.
    ///
    /// Sichtbar war davon NICHTS: die Anzeige sagte brav "upgrade, step 7 -- one of:
    /// ..." und sah aus wie die vorgesehene Auskunft fuer einen unbekannten Fall.
    /// Genau deshalb wird hier auf den NAMEN geprueft und nicht darauf, dass etwas
    /// zurueckkommt.
    /// </remarks>
    private static void CheckPartNames()
    {
        // Ohne den Vorrat ist nichts zu pruefen -- er wird aus dem laufenden Spiel
        // gewonnen und liegt nicht in jedem Arbeitsordner.
        if (Tuning.PartNames.Name("Clutch", 0) is null) { return; }

        (string Label, int Step, string Erwartet)[] faelle =
        [
            ("Clutch", 0, "Stock Clutch"),
            ("Clutch", 1, "Street Clutch"),
            ("Clutch", 2, "Sport Clutch"),
            ("Clutch", 3, "Race Clutch"),
            ("Brakes", 3, "Race Brakes"),
            ("Transmission", 3, "Race Transmission"),
            // Die Zahl im Namen ist die Stufe -- vom Nutzer bestaetigt.
            ("Transmission", 7, "Race Transmission: 7 Speed"),
            ("Transmission", 4, "Drift Transmission: 4 Speed"),
            // Und der einzige Name ohne Zahl kann nur auf der einzigen freien
            // Stufe sitzen.
            ("Transmission", 5, "Rally Transmission"),
        ];
        foreach (var (label, step, erwartet) in faelle)
        {
            var gelesen = Tuning.PartNames.Name(label, step);
            if (gelesen != erwartet)
            {
                throw new InvalidOperationException(
                    $"{label} Stufe {step} kam als '{gelesen ?? "(nichts)"}' statt "
                    + $"'{erwartet}'.");
            }
        }

        // Wo die Zuordnung NICHT belegt ist, darf auch kein Name behauptet werden:
        // drei Differential-Sondernamen ohne Zahl auf drei Stufen sind nicht zu
        // trennen.
        if (Tuning.PartNames.Name("Differential", 5) is { } erfunden)
        {
            throw new InvalidOperationException(
                $"Fuer das Differential auf Stufe 5 wurde '{erfunden}' behauptet, "
                + "obwohl drei Namen ohne Zahl zur Wahl stehen.");
        }
    }

    private static void AssertNear(double actual, double expected, string name,
                                   double toleranz = 0.001)
    {
        if (Math.Abs(actual - expected) > toleranz)
        {
            throw new InvalidOperationException(
                $"{name} failed: expected {expected:F3}, got {actual:F3}.");
        }
    }

    private static uint ComputeDualSenseBluetoothCrc(byte[] report)
    {
        var crc = 0xFFFF_FFFFu;
        crc = StepCrc(crc, 0xA2);
        for (var index = 0; index < 74; index++)
        {
            crc = StepCrc(crc, report[index]);
        }

        return ~crc;
    }

    private static uint StepCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0
                ? (crc >> 1) ^ 0xEDB8_8320u
                : crc >> 1;
        }

        return crc;
    }
}
