using System;
using System.Text;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace Quartermaster
{
    // Shared control factories so screens have a consistent look (mirrors Shipyard's Frame).
    static class Frame
    {
        // A fixed-size Rectangular button (the debug base's AddButton ignores Size).
        public static MyGuiControlButton MakeButton(string text, Vector2 pos, Vector2 size, Action<MyGuiControlButton> onClick)
            => new MyGuiControlButton(position: pos, visualStyle: MyGuiControlButtonStyleEnum.Rectangular,
                size: size, text: new StringBuilder(text), onButtonClick: onClick);

        // A horizontally-centered label at panel-relative height y.
        public static MyGuiControlLabel CenterLabel(string text, float y, Vector4 color, float scale)
            => new MyGuiControlLabel(new Vector2(0f, y), null, text, color, scale)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER };
    }
}
