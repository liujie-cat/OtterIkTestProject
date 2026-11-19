using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace OtterLocomotion.EditorTools
{
    /// <summary>
    /// Plays scripted mouse input through PointerGestures and GearMachine against a body held still facing +z, and
    /// checks each rule of the controls (行动操作). Writes Logs/Controls_SelfTest.txt; needs no scene and no Play.
    /// </summary>
    static class ControlsSelfTest
    {
        const float Frame = 1f / 60f;

        class Sim
        {
            public readonly GestureSettings gs = new GestureSettings();
            public readonly GearSettings settings = new GearSettings();
            public readonly GearMachine gears = new GearMachine();
            public readonly List<ControlEvent> events = new List<ControlEvent>();
            readonly PointerGestures pointer = new PointerGestures();
            readonly List<Gesture> gestures = new List<Gesture>();
            public float time, speedBL;
            public bool airborne;
            public Vector3 point = new Vector3(0f, 0f, 1f);

            public Sim(Gear start = Gear.Park) => gears.Reset(Body, start);

            GearBody Body => new GearBody { position = Vector3.zero, forward = Vector3.forward, speedBL = speedBL, airborne = airborne };

            public Sim Run(float seconds, bool left = false, bool right = false, bool onOtter = false)
            {
                for (float t = 0f; t < seconds - 1e-5f; t += Frame)
                {
                    gestures.Clear();
                    Vector3 at = onOtter ? Vector3.zero : point;
                    pointer.Step(gs, time, left, right, at, onOtter, gestures);
                    gears.Step(settings, gs, gestures, time, at, onOtter, Body, events);
                    time += Frame;
                }
                return this;
            }

            public Sim Wait(float seconds) => Run(seconds);
            public Sim Click() => Run(0.1f, left: true).Run(Frame);
            public Sim DoubleClick() => Click().Wait(0.1f).Click();
            public Sim Hold(float seconds) => Run(seconds, left: true).Run(Frame);
            public Sim RightTap() => Run(0.1f, right: true).Run(Frame);

            public Sim Drag(float seconds, Vector3 to)
            {
                Run(Frame, left: true, onOtter: true);
                point = to;
                return Run(seconds, left: true);
            }

            public int Kicks => events.Count(e => e.kind == ControlEventKind.Kick);
            public int Jumps => events.Count(e => e.kind == ControlEventKind.Jump);
            public override string ToString() =>
                $"gear {gears.Gear}{(gears.Dolphin ? " wave" : "")}, {gears.Posture}, {gears.Gait}, {gears.SpeedBL:F2} BL/s; " +
                $"events [{string.Join(", ", events)}]";
        }

        // sprinting in the wave, the long press still held
        static Sim InWave()
        {
            var s = new Sim(Gear.Cruise) { speedBL = 1.9f };
            return s.Run(0.4f, left: true);
        }

        [MenuItem("Tools/Otter Locomotion/Self-Test Controls")]
        static void Run()
        {
            var lines = new List<string>();
            int failed = 0;
            void Check(string name, bool ok, Sim s)
            {
                lines.Add($"{(ok ? "PASS" : "FAIL")}  {name}: {s}");
                if (!ok) failed++;
            }

            var s = new Sim().Click().Wait(0.5f);
            Check("停驻单击：蹬一次轻的，仍停驻仰姿", s.Kicks == 1 && s.events[0].strength == s.settings.parkKick && s.gears.Gear == Gear.Park && s.gears.Posture == Posture.Supine, s);

            s = new Sim().DoubleClick().Wait(0.5f);
            Check("停驻双击：第一下蹬腿，第二下升入慢游", s.Kicks == 1 && s.gears.Gear == Gear.Slow && s.gears.Posture == Posture.Supine && s.gears.Gait == Gait.SupineAlternate, s);

            s = new Sim().DoubleClick().Click().Wait(0.5f);
            Check("连点三下：第三下另起，只蹬腿不再升档", s.Kicks == 2 && s.gears.Gear == Gear.Slow, s);

            s = new Sim(Gear.Slow).DoubleClick().Wait(0.5f);
            Check("慢游双击：第一下蹬腿，第二下升入巡航并翻成俯身", s.Kicks == 1 && s.gears.Gear == Gear.Cruise && s.gears.Posture == Posture.Prone && s.gears.Gait == Gait.ProneTogether, s);

            s = new Sim(Gear.Cruise).Click().Wait(0.5f);
            Check("巡航单击：蹬一次，仍巡航", s.Kicks == 1 && s.gears.Gear == Gear.Cruise, s);

            s = new Sim(Gear.Cruise) { speedBL = 1.2f }.Run(0.5f, left: true);
            bool kicking = s.gears.Gear == Gear.Sprint && s.gears.Accelerating && !s.gears.Dolphin && s.gears.Gait == Gait.ProneTogether && s.gears.SpeedBL == s.settings.sprintSpeedBL;
            s.speedBL = 1.85f;
            s.Run(0.1f, left: true);
            Check("巡航长按：加速，先俯身蹬腿，到冲刺速度后转海豚", kicking && s.gears.Dolphin && s.gears.Gait == Gait.Dolphin, s);

            s = new Sim(Gear.Cruise) { speedBL = 1.2f }.Run(0.5f, left: true).Run(1.5f, left: true);
            Check("长按蹬腿一直到不了冲刺速度：dolphinAfter 秒后也转海豚", s.gears.Dolphin, s);

            s = new Sim(Gear.Cruise) { speedBL = 1.2f }.Hold(0.6f).Wait(0.5f);
            Check("没到海豚就松手：继续俯身巡航，松手不算单击", s.gears.Gear == Gear.Cruise && s.Kicks == 0, s);

            s = InWave().Run(Frame);
            Check("海豚段松手：停止加速，仍在海豚段", s.gears.Dolphin && !s.gears.Accelerating && s.gears.SpeedBL == s.settings.cruiseSpeedBL, s);

            s = InWave().Run(Frame).DoubleClick().Wait(0.5f);
            Check("海豚段双击：起跳一次，两下都不蹬腿", s.Jumps == 1 && s.Kicks == 0, s);

            s = InWave().Run(Frame);
            s.speedBL = 1.2f;
            s.Wait(0.05f).DoubleClick().Wait(0.5f);
            Check("松手后已减速，但在一个双击窗口内：仍可起跳", s.Jumps == 1 && s.Kicks == 0, s);

            s = InWave().Run(Frame);
            s.speedBL = 1.2f;
            s.Wait(1f);
            bool cruising = s.gears.Gear == Gear.Cruise && s.gears.Posture == Posture.Prone;
            s.DoubleClick().Wait(0.5f);
            Check("宽限过后慢下来：回到巡航，双击只蹬腿不起跳", cruising && s.Jumps == 0 && s.Kicks == 2, s);

            s = InWave().Run(Frame);
            s.speedBL = 1.9f;
            s.Wait(1.5f).DoubleClick().Wait(0.5f);
            Check("松手很久但还在冲刺速度：仍可起跳", s.Jumps == 1, s);

            s = InWave().Run(Frame).Click().RightTap().Click().Wait(0.5f);
            Check("右键取消起跳资格：等待中的第一下作废，第二下按巡航蹬腿", s.Jumps == 0 && s.Kicks == 1 && s.gears.Gear == Gear.Cruise, s);

            s = new Sim(Gear.Cruise).RightTap();
            bool slow = s.gears.Gear == Gear.Slow && s.gears.Posture == Posture.Supine;
            s.RightTap().RightTap();
            Check("右键点按：每下立即降一档，退出巡航翻回仰姿，停驻不再降", slow && s.gears.Gear == Gear.Park, s);

            s = new Sim(Gear.Cruise).Run(0.5f, right: true);
            bool braking = s.gears.Braking && s.gears.Gear == Gear.Park && s.gears.SpeedBL == 0f;
            s.Run(Frame);
            Check("右键按住：刹水至停驻，松开结束刹水", braking && !s.gears.Braking, s);

            s = new Sim(Gear.Slow).Drag(0.2f, new Vector3(-1f, 0f, 0f));
            bool aimed = s.gears.Steering && Vector3.Angle(s.gears.Heading, Vector3.left) < 1f && s.gears.AimBlend == 1f;
            s.Run(Frame).Wait(s.settings.aimFade + 0.05f);
            Check("拖动转向：朝光标，松手后瞄准淡出，不改档", aimed && !s.gears.Steering && s.gears.AimBlend == 0f && s.gears.Gear == Gear.Slow, s);

            s = new Sim().Drag(0.2f, new Vector3(-1f, 0f, 0f));
            Check("停驻拖动大角度转弯：带一点漂移", s.gears.SpeedBL == s.settings.parkDriftBL, s);

            s = new Sim().Drag(0.2f, new Vector3(1f, 0f, 0f)).Run(Frame).Click().Wait(0.5f);
            Check("拖动松手后紧接单击：不算双击，只蹬腿", s.Kicks == 1 && s.gears.Gear == Gear.Park, s);

            s = new Sim().Hold(0.5f).Click().Wait(0.5f);
            Check("停驻长按无效果；长按松手后紧接单击不算双击", s.Kicks == 1 && s.gears.Gear == Gear.Park, s);

            s = new Sim() { point = new Vector3(1f, 0f, 0f) }.Click();
            Check("单击朝点击方向：航向转向点击处", Vector3.Angle(s.gears.Heading, Vector3.right) < 1f && Vector3.Angle(s.events[0].direction, Vector3.right) < 1f, s);

            s = InWave().Run(Frame);
            s.airborne = true;
            s.DoubleClick().Click().Wait(0.5f);
            Check("空中的点击作废", s.Jumps == 0 && s.Kicks == 0, s);

            lines.Insert(0, $"# controls self-test {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}: {lines.Count - failed}/{lines.Count} pass");
            Directory.CreateDirectory("Logs");
            File.WriteAllLines(Path.Combine("Logs", "Controls_SelfTest.txt"), lines);
            Debug.Log($"[ControlsSelfTest] {lines.Count - 1 - failed}/{lines.Count - 1} pass, see Logs/Controls_SelfTest.txt");
        }
    }
}
