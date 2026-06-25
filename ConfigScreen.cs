using System;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace Quartermaster
{
    // Ctrl+Shift+Home config menu: destination URL, online/offline sinks, sync frequency, the Steam
    // "Link account" onboarding, and a live link-status line.
    public class ConfigScreen : MyGuiScreenDebugBase
    {
        private readonly QmConfig _cfg;
        private MyGuiControlTextbox _url, _freq, _token;
        private MyGuiControlCheckbox _online, _offline, _chat;
        private MyGuiControlCombobox _auth;
        private static readonly string[] AuthModes = { "none", "bearer", "oauth2_cc" };

        private const float LabelX = -0.27f;
        private const float CtrlX = -0.05f;
        private const float RowH = 0.035f;

        public ConfigScreen(QmConfig cfg)
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.66f, 0.74f), Brand.Bg, isTopMostScreen: false)
        {
            _cfg = cfg;
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "QuartermasterConfig";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);

            Center(Brand.Faction, -0.335f, Brand.Accent, 0.9f);
            Center(Brand.Product, -0.305f, Brand.AccentDim, 0.72f);
            Center("Configure where your fleet logistics data is sent.", -0.272f, Brand.Muted, 0.68f);

            // ---- link status + onboarding ----
            Vector4 sc; string st = StatusText(out sc);
            Center(st, -0.232f, sc, 0.82f);
            MakeBtn("Link account  (Steam)", new Vector2(0f, -0.188f), new Vector2(0.42f, 0.042f),
                () => { CloseScreen(false); Onboard.Begin(_cfg); });

            // ---- endpoint ----
            AddLabel("Destination URL:", -0.123f);
            _url = AddBox(-0.123f, _cfg.EndpointUrl, 0.31f);

            // ---- auth (common case settable here; oauth2_cc IdP details stay in config.json) ----
            AddLabel("Auth mode:", -0.078f);
            _auth = AddCombo(-0.078f, 0.31f, _cfg.AuthMode);
            AddLabel("Token (blank=keep):", -0.033f);
            _token = AddBox(-0.033f, "", 0.31f);

            // ---- sinks + rate ----
            _online = AddCheck(0.016f, _cfg.Online, "Send online (POST to the URL above)");
            _offline = AddCheck(0.056f, _cfg.Offline, "Also write an offline batch file");
            AddLabel("Sync every (seconds):", 0.105f);
            _freq = AddBox(0.105f, ((int)Math.Round(_cfg.ScanIntervalSeconds)).ToString(), 0.1f);
            _chat = AddCheck(0.15f, _cfg.ChatOnSync, "Announce each automatic sync in chat");

            // ---- actions ----
            MakeBtn("Sync now", new Vector2(-0.12f, 0.215f), new Vector2(0.2f, 0.044f),
                () => { Plugin.Instance?.ManualSync(); });
            MakeBtn("Save", new Vector2(0.12f, 0.215f), new Vector2(0.2f, 0.044f), OnSave);
            MakeBtn("Close", new Vector2(0f, 0.272f), new Vector2(0.42f, 0.04f), () => CloseScreen(false));

            Center(Brand.Classified, 0.318f, Brand.AccentDim, 0.55f);
        }

        private string StatusText(out Vector4 color)
        {
            string mode = (_cfg.AuthMode ?? "none").ToLowerInvariant();
            if (mode == "none") { color = Brand.Muted; return "Auth: none (open endpoint)"; }
            if (string.IsNullOrEmpty(_cfg.TokenPlain)) { color = Brand.Warn; return "NOT LINKED  -  press Link account"; }
            int code = SyncStatus.LastOnlineCode;
            if (code == 200) { color = Brand.Ok; return "LINKED  -  last sync OK"; }
            if (code == 401 || code == 403) { color = Brand.Warn; return "TOKEN REJECTED (" + code + ")  -  re-link"; }
            if (code < 0) { color = Brand.Warn; return "NETWORK ERROR reaching server"; }
            color = Brand.AccentDim; return "Linked  -  awaiting first sync";
        }

        private void OnSave()
        {
            _cfg.EndpointUrl = (_url.Text ?? "").Trim();
            _cfg.Online = _online.IsChecked;
            _cfg.Offline = _offline.IsChecked;
            double s;
            if (double.TryParse((_freq.Text ?? "").Trim(), out s)) _cfg.ScanIntervalSeconds = Math.Max(1.0, s);
            _cfg.ChatOnSync = _chat.IsChecked;

            long k = _auth.GetSelectedKey();
            _cfg.AuthMode = (k >= 0 && k < AuthModes.Length) ? AuthModes[k] : "none";
            var tok = (_token.Text ?? "").Trim();
            if (tok.Length > 0) _cfg.TokenPlain = tok;   // only overwrite when the user typed a new token

            _cfg.Save();
            Plugin.Instance?.OnConfigChanged();
            Notify.Hud("Quartermaster: settings saved", 2500);
            RecreateControls(false);
        }

        void Center(string text, float y, Vector4 color, float scale) => Controls.Add(Frame.CenterLabel(text, y, color, scale));

        void AddLabel(string text, float y)
            => Controls.Add(new MyGuiControlLabel(new Vector2(LabelX, y), null, text)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });

        MyGuiControlTextbox AddBox(float y, string text, float width)
        {
            var box = new MyGuiControlTextbox
            {
                Position = new Vector2(CtrlX, y),
                Size = new Vector2(width, RowH),
                OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER
            };
            if (!string.IsNullOrEmpty(text)) box.Text = text;
            Controls.Add(box);
            return box;
        }

        MyGuiControlCombobox AddCombo(float y, float width, string current)
        {
            var c = new MyGuiControlCombobox(new Vector2(CtrlX, y), new Vector2(width, RowH))
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
            long sel = 0;
            for (long i = 0; i < AuthModes.Length; i++)
            {
                c.AddItem(i, MyStringId.GetOrCompute(AuthModes[i]));
                if (string.Equals(AuthModes[i], current, StringComparison.OrdinalIgnoreCase)) sel = i;
            }
            c.SelectItemByKey(sel);
            Controls.Add(c);
            return c;
        }

        MyGuiControlCheckbox AddCheck(float y, bool init, string label)
        {
            var cb = new MyGuiControlCheckbox(new Vector2(CtrlX, y))
            { IsChecked = init, OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER };
            Controls.Add(cb);
            Controls.Add(new MyGuiControlLabel(new Vector2(CtrlX + 0.035f, y), null, label)
            { OriginAlign = MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER });
            return cb;
        }

        MyGuiControlButton MakeBtn(string text, Vector2 pos, Vector2 size, Action onClick)
        {
            var b = Frame.MakeButton(text, pos, size, _ => onClick());
            Controls.Add(b);
            return b;
        }
    }
}
