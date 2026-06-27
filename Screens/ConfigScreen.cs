using System;
using Sandbox.Game.Gui;
using Sandbox.Graphics.GUI;
using VRage.Game;
using VRage.Utils;
using VRageMath;

namespace Quartermaster
{
    // Ctrl+Shift+Home config menu: destination URL, auth (mode-adaptive: bearer token, or full oauth2_cc
    // client config), online/offline sinks, sync frequency, the Steam "Link account" onboarding + Wipe auth,
    // and a live status line.
    public class ConfigScreen : MyGuiScreenDebugBase
    {
        private readonly QmConfig _cfg;
        private MyGuiControlTextbox _url, _freq, _token, _tokenUrl, _clientId, _clientSecret, _scope;
        private MyGuiControlCheckbox _online, _offline, _chat;
        private string _authMode;
        private static readonly string[] AuthModes = { "none", "bearer", "oauth2_cc" };

        private const float LabelX = -0.27f;
        private const float CtrlX = -0.05f;
        private const float RowH = 0.035f;

        public ConfigScreen(QmConfig cfg)
            : base(new Vector2(0.5f, 0.5f), new Vector2(0.66f, 0.80f), Brand.Bg, isTopMostScreen: false)
        {
            _cfg = cfg;
            _authMode = string.IsNullOrEmpty(cfg.AuthMode) ? "none" : cfg.AuthMode.Trim().ToLowerInvariant();
            RecreateControls(true);
        }

        public override string GetFriendlyName() => "QuartermasterConfig";

        public override void RecreateControls(bool constructor)
        {
            base.RecreateControls(constructor);
            _token = _tokenUrl = _clientId = _clientSecret = _scope = null;   // mode-dependent; reset stale refs

            Center(Brand.Faction, -0.37f, Brand.Accent, 0.9f);
            Center(Brand.Product, -0.342f, Brand.AccentDim, 0.72f);
            Center("Configure where your fleet logistics data is sent.", -0.314f, Brand.Muted, 0.66f);

            // ---- status + onboarding ----
            Vector4 sc; string st = StatusText(out sc);
            Center(st, -0.274f, sc, 0.8f);
            MakeBtn("Link account (Steam)", new Vector2(-0.105f, -0.228f), new Vector2(0.30f, 0.042f),
                () => { CloseScreen(false); Onboard.Begin(_cfg); });
            MakeBtn("Wipe auth", new Vector2(0.145f, -0.228f), new Vector2(0.16f, 0.042f), WipeAuth);

            // ---- endpoint ----
            AddLabel("Destination URL:", -0.173f);
            _url = AddBox(-0.173f, _cfg.EndpointUrl, 0.31f);

            // ---- auth mode (cycle) + mode-adaptive fields in the reserved area below ----
            AddLabel("Auth mode:", -0.128f);
            MakeBtn("<  " + _authMode + "  >", new Vector2(0.105f, -0.128f), new Vector2(0.31f, 0.038f), CycleAuth);

            if (_authMode == "bearer")
            {
                AddLabel("Token (blank=keep):", -0.083f);
                _token = AddBox(-0.083f, "", 0.31f);
            }
            else if (_authMode == "oauth2_cc")
            {
                AddLabel("Token URL:", -0.083f);          _tokenUrl = AddBox(-0.083f, _cfg.TokenUrl, 0.31f);
                AddLabel("Client ID:", -0.041f);          _clientId = AddBox(-0.041f, _cfg.ClientId, 0.31f);
                AddLabel("Secret (blank=keep):", 0.001f); _clientSecret = AddBox(0.001f, "", 0.31f);
                AddLabel("Scope (optional):", 0.043f);    _scope = AddBox(0.043f, _cfg.OAuthScope, 0.31f);
            }

            // ---- sinks + rate ----
            _online = AddCheck(0.09f, _cfg.Online, "Send online (POST to the URL above)");
            _offline = AddCheck(0.128f, _cfg.Offline, "Also write an offline batch file");
            AddLabel("Sync every (seconds):", 0.166f);
            _freq = AddBox(0.166f, ((int)Math.Round(_cfg.ScanIntervalSeconds)).ToString(), 0.1f);
            _chat = AddCheck(0.204f, _cfg.ChatOnSync, "Announce each automatic sync in chat");

            // ---- actions ----
            MakeBtn("Sync now", new Vector2(-0.12f, 0.258f), new Vector2(0.2f, 0.044f),
                () => { Plugin.Instance?.ManualSync(); });
            MakeBtn("Save", new Vector2(0.12f, 0.258f), new Vector2(0.2f, 0.044f), OnSave);
            MakeBtn("Close", new Vector2(0f, 0.312f), new Vector2(0.42f, 0.04f), () => CloseScreen(false));

            Center(Brand.Classified, 0.355f, Brand.AccentDim, 0.55f);
        }

        private string StatusText(out Vector4 color)
        {
            string mode = _authMode ?? "none";
            if (mode == "none") { color = Brand.Muted; return "Auth: none (open endpoint)"; }
            if (mode == "oauth2_cc")
            {
                if (string.IsNullOrEmpty(_cfg.TokenUrl) || string.IsNullOrEmpty(_cfg.ClientId) || string.IsNullOrEmpty(_cfg.ClientSecretPlain))
                { color = Brand.Warn; return "OAuth2: set Token URL + Client ID + secret"; }
            }
            else if (string.IsNullOrEmpty(_cfg.TokenPlain))   // bearer
            { color = Brand.Warn; return "NOT LINKED  -  Link account or set a token"; }

            int code = SyncStatus.LastOnlineCode;
            if (code == 200) { color = Brand.Ok; return "OK  -  last sync 200"; }
            if (code == 401 || code == 403) { color = Brand.Warn; return "TOKEN REJECTED (" + code + ")  -  re-auth"; }
            if (code < 0) { color = Brand.Warn; return "NETWORK ERROR reaching server"; }
            color = Brand.AccentDim; return "Configured  -  awaiting first sync";
        }

        // Read the currently-visible controls into _cfg (in memory only; not persisted). Run before a mode
        // switch so in-progress edits survive the layout change, and by Save before writing to disk.
        private void CaptureEdits()
        {
            if (_url != null) _cfg.EndpointUrl = (_url.Text ?? "").Trim();
            if (_online != null) _cfg.Online = _online.IsChecked;
            if (_offline != null) _cfg.Offline = _offline.IsChecked;
            if (_freq != null) { double s; if (double.TryParse((_freq.Text ?? "").Trim(), out s)) _cfg.ScanIntervalSeconds = Math.Max(1.0, s); }
            if (_chat != null) _cfg.ChatOnSync = _chat.IsChecked;
            if (_token != null) { var t = (_token.Text ?? "").Trim(); if (t.Length > 0) _cfg.TokenPlain = t; }   // blank = keep
            if (_tokenUrl != null) _cfg.TokenUrl = (_tokenUrl.Text ?? "").Trim();
            if (_clientId != null) _cfg.ClientId = (_clientId.Text ?? "").Trim();
            if (_clientSecret != null) { var s = (_clientSecret.Text ?? "").Trim(); if (s.Length > 0) _cfg.ClientSecretPlain = s; }   // blank = keep
            if (_scope != null) _cfg.OAuthScope = (_scope.Text ?? "").Trim();
        }

        private void OnSave()
        {
            CaptureEdits();
            _cfg.AuthMode = _authMode;
            _cfg.Save();
            Plugin.Instance?.OnConfigChanged();
            Notify.Hud("Quartermaster: settings saved", 2500);
            RecreateControls(false);
        }

        // Cycle none -> bearer -> oauth2_cc. A combobox renders unreliably in this debug screen, so a button
        // reuses the proven path. Captures edits first so switching modes doesn't lose what you've typed.
        private void CycleAuth()
        {
            CaptureEdits();
            int i = Array.IndexOf(AuthModes, _authMode);
            _authMode = AuthModes[(i + 1) % AuthModes.Length];   // i == -1 (unknown) -> 0 = "none"
            RecreateControls(false);
        }

        // Clear stored credentials and reset to no-auth, persisted immediately — resets a stale/wrong link or a
        // backend switch without hand-editing config.json. Leaves the (non-secret) URL/ClientID in place.
        private void WipeAuth()
        {
            CaptureEdits();
            _authMode = "none";
            _cfg.AuthMode = "none";
            _cfg.TokenPlain = "";
            _cfg.ClientSecretPlain = "";
            _cfg.Save();
            Plugin.Instance?.OnConfigChanged();
            Notify.Hud("Quartermaster: auth wiped (mode=none, secrets cleared)", 3000);
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
