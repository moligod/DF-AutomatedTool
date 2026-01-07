using System;
using System.Drawing;

namespace WindowSpy
{
    public enum ActionType { Click, Ocr, Condition, Save, Expression, LoopStart, LoopEnd, BringFront }
    public enum TargetType { A, B }

    public class ScriptStep
    {
        public ActionType Type { get; set; }
        public TargetType Target { get; set; }
        public Rectangle Rect { get; set; }
        public Point Point { get; set; }
        public int DelayMs { get; set; }
        public int RandomDelay { get; set; } = 0;
        public int DwellMs { get; set; }
        public int RandomDwell { get; set; } = 0;
        public int RandomX { get; set; } = 0;
        public int RandomY { get; set; } = 0;
        public string Pattern { get; set; } = "";
        public string Key { get; set; } = "";
        public int Count { get; set; }
        public bool? LastResult { get; set; } = null;
        public bool JumpOnTrue { get; set; } = false;
    }
}
