using System;
using System.Collections;
using System.Text;
using Mmo.Net;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Debug = UnityEngine.Debug;

namespace HotUpdate.Ui
{
    /// <summary>
    /// 入口四屏的流程：登入 → 立身 → 角色栏 → 载入。
    ///
    /// 一件事只在这一处做：把服务端的回包翻译成「下一步去哪」。
    /// 界面不判口令对不对、不判名号能不能用、也不判能不能进世界——
    /// 只有回包说成，才往下走；载入进度条走得再满，也不代表能进游戏。
    ///
    /// 会话只保存在内存里，不落盘：不记住口令、不做自动登录、不存令牌
    /// （原型「刻意不做」那一条）。网关地址与战斗那条一样是本机常量，
    /// 真机联调时改这一处。
    /// </summary>
    public sealed class UiEntryFlow : MonoBehaviour
    {
        /// <summary>流程就绪。</summary>
        public const string ReadyLog = "u2_entry=ready";

        /// <summary>网关地址。默认本机直连，与 `config/gateway.json` 的监听口一致。</summary>
        public static string Gateway = "http://127.0.0.1:61000";

        /// <summary>发一条请求。状态 0 表示没到网关；测试可以换掉它。</summary>
        public delegate void EntrySend(string path, string body, string token, Action<int, string> done);

        /// <summary>入口四屏各是哪一屏。日志与核对按这个说。</summary>
        public enum Step
        {
            None,
            Login,
            Create,
            Roster,
            Loading,
        }

        UiShell _shell;

        EntrySend _send;

        UiTextField _account;

        UiTextField _password;

        UiTextField _name;

        Text _loginHint;

        Text _createHint;

        UiButton _loginButton;

        UiButton _createButton;

        Func<bool> _createReady;

        UiButton _enterButton;

        UiLoadingScreen _loading;

        float _progress;

        /// <summary>当前在哪一屏。</summary>
        public Step Current { get; private set; }

        /// <summary>有请求在路上。按钮据此置灰，防重复点。</summary>
        public bool Busy { get; private set; }

        /// <summary>登录拿到的令牌。只在内存里，进程结束就没了。</summary>
        public string Token { get; private set; } = string.Empty;

        /// <summary>最近一次回包的种类。</summary>
        public EntryKind LastKind { get; private set; } = EntryKind.Rejected;

        /// <summary>建出来的角色。角色栏与载入只读这一份回包。</summary>
        public CreateOutcome Character { get; private set; }

        /// <summary>服务端确认可以进世界了。载入屏看它，不看进度条。</summary>
        public bool Entered { get; private set; }

        /// <summary>已经发了几条请求。空账号空口令时必须是 0。</summary>
        public int Requests { get; private set; }

        /// <summary>载入屏当前的进度，只作表现。</summary>
        public float Progress => _progress;

        /// <summary>取流程，没有就建一个。整个客户端只该有一份。</summary>
        public static UiEntryFlow Ensure(UiShell shell)
        {
            var existing = FindObjectOfType<UiEntryFlow>();
            if (existing != null)
            {
                existing.Init(shell);
                return existing;
            }

            var go = new GameObject("UiEntryFlow", typeof(UiEntryFlow));
            var flow = go.GetComponent<UiEntryFlow>();
            flow.Init(shell);
            return flow;
        }

        /// <summary>显式初始化。编辑模式不触发 <c>Awake</c>，所以不靠它。幂等。</summary>
        public void Init(UiShell shell)
        {
            if (_shell != null)
            {
                return;
            }

            _shell = shell;
            _send = SendByUnityWebRequest;
            Debug.Log(ReadyLog + " gateway=" + Gateway + " steps=4");
        }

        /// <summary>换掉发送实现。核对时喂固定回包，避免真连网关。</summary>
        public void SetSender(EntrySend send)
        {
            _send = send ?? SendByUnityWebRequest;
        }

        /// <summary>回到干净状态。核对每段之间用。</summary>
        public void Reset()
        {
            Current = Step.None;
            Busy = false;
            Token = string.Empty;
            LastKind = EntryKind.Rejected;
            Character = default(CreateOutcome);
            Entered = false;
            Requests = 0;
            _progress = 0f;
            _loading = null;
            _account = null;
            _password = null;
            _name = null;
            _loginHint = null;
            _createHint = null;
            _loginButton = null;
            _createButton = null;
            _enterButton = null;
        }

        // ------------------------------------------------------------------ 各屏入口

        /// <summary>开登入屏（A01）。</summary>
        public void OpenLogin()
        {
            Current = Step.Login;
            _shell.ShowPage(string.Empty, shell => UiLoginScreen.Build(shell, this));
        }

        /// <summary>开立身屏（A02）。登录成功才走到这里。</summary>
        public void OpenCreate()
        {
            Current = Step.Create;
            _shell.ShowPage("立身 · 选出身与职责", shell => UiCreateScreen.Build(shell, this));
        }

        /// <summary>开角色栏（A04）。建角成功才走到这里。</summary>
        public void OpenRoster()
        {
            Current = Step.Roster;
            _shell.ShowPage("角色栏", shell => UiRosterScreen.Build(shell, this));
        }

        /// <summary>开载入屏（A03）。选定角色之后才走到这里。</summary>
        public void OpenLoading()
        {
            Current = Step.Loading;
            Entered = false;
            _progress = 0f;
            var verse = UiEntryCodex.NextVerse();
            _shell.ShowPage(string.Empty, shell => _loading = UiLoadingScreen.Build(shell, verse));
        }

        // ------------------------------------------------------------------ 登入

        /// <summary>登入屏把控件交给流程，流程负责启用与提示。</summary>
        public void BindLogin(
            UiTextField account, UiTextField password, Text hint, UiButton submit)
        {
            _account = account;
            _password = password;
            _loginHint = hint;
            _loginButton = submit;

            account.OnChanged = SyncLoginButton;
            password.OnChanged = SyncLoginButton;
            SyncLoginButton();
        }

        /// <summary>核对用：等价于在登入两栏里打字。界面自己不走这条路。</summary>
        public void FillLogin(string account, string password)
        {
            if (_account != null)
            {
                _account.SetText(account);
            }

            if (_password != null)
            {
                _password.SetText(password);
            }

            SyncLoginButton();
        }

        /// <summary>核对用：填名号。</summary>
        public void FillName(string name)
        {
            if (_name != null)
            {
                _name.SetText(name);
            }

            SyncCreateButton();
        }

        /// <summary>
        /// 发登录请求。空账号或空口令就地拦住、不发请求（原型状态清单第一条），
        /// 这不算判口令——真正的结论只认回包。
        /// </summary>
        public void SubmitLogin()
        {
            if (Busy || _account == null)
            {
                return;
            }

            var account = _account.Text.Trim();
            var password = _password.Text;
            if (account.Length == 0 || password.Length == 0)
            {
                return;
            }

            SetHint(_loginHint, string.Empty);
            Busy = true;
            SyncLoginButton();

            var body = AccountEntry.LoginBody(account, password);
            Debug.Log("u2_login=send path=/login account=" + account + " pw_len=" + password.Length);
            Send("/login", body, null, (status, reply) =>
            {
                Busy = false;
                var outcome = AccountEntry.ParseLogin(status, reply);
                LastKind = outcome.Kind;
                Debug.Log("u2_login=reply status=" + status
                    + " kind=" + AccountEntry.KindName(outcome.Kind)
                    + " token_len=" + (outcome.Token == null ? 0 : outcome.Token.Length));

                if (outcome.Ok)
                {
                    Token = outcome.Token;
                    Character = default(CreateOutcome);
                    OpenCreate();
                    return;
                }

                // 失败留在原屏：输入还在，口令不清，只多一行提示。
                SyncLoginButton();
                SetHint(_loginHint, HintOf(outcome.Kind));
            });
        }

        void SyncLoginButton()
        {
            if (_loginButton == null)
            {
                return;
            }

            var filled = _account != null && _password != null
                && _account.Text.Trim().Length > 0 && _password.Text.Length > 0;
            _loginButton.Enabled = !Busy && filled;
            _loginButton.Label.text = Busy ? "正在校验" : "登 入";
            _loginButton.Apply(_shell.Palette);
        }

        // ------------------------------------------------------------------ 立身

        /// <summary>
        /// 立身屏把控件交给流程。<paramref name="ready"/> 由屏自己给：
        /// 出身、职责、名号三样齐了才能按，缺一样就置灰（不算判合法性）。
        /// </summary>
        public void BindCreate(UiTextField name, Text hint, UiButton submit, Func<bool> ready)
        {
            _name = name;
            _createHint = hint;
            _createButton = submit;
            _createReady = ready;
        }

        /// <summary>立身屏每次改选择都调一下，按钮的可用态与文案跟着走。</summary>
        public void SyncCreateButton()
        {
            if (_createButton == null)
            {
                return;
            }

            _createButton.Enabled = !Busy && _createReady != null && _createReady();
            _createButton.Label.text = Busy ? "正在立身" : "入 世";
            _createButton.Apply(_shell.Palette);
        }

        /// <summary>
        /// 发建角请求。出身与职业只发编号，外观按出身表给，分支固定留空
        /// （15 级一转才选，创角不列）。等级、境界、阵营一个都不发。
        /// </summary>
        public void SubmitCreate(string raceId, string classId, string name)
        {
            if (Busy)
            {
                return;
            }

            var race = UiEntryCodex.FindRace(raceId);
            var role = UiEntryCodex.FindRole(classId);
            var trimmed = (name ?? string.Empty).Trim();
            if (race == null || role == null || trimmed.Length == 0 || Token.Length == 0)
            {
                return;
            }

            SetHint(_createHint, string.Empty);
            Busy = true;
            SyncCreateButton();

            var body = AccountEntry.CreateBody(trimmed, race.Value.Id, race.Value.Appearance, role.Value.Id);
            Debug.Log("u2_create=send path=/character race=" + race.Value.Id
                + " class=" + role.Value.Id + " name_len=" + trimmed.Length + " branch_empty=true");
            Send("/character", body, Token, (status, reply) =>
            {
                Busy = false;
                var outcome = AccountEntry.ParseCreate(status, reply);
                LastKind = outcome.Kind;
                Debug.Log("u2_create=reply status=" + status
                    + " kind=" + AccountEntry.KindName(outcome.Kind)
                    + " id=" + outcome.CharacterId);

                if (outcome.Ok)
                {
                    Character = outcome;
                    OpenRoster();
                    return;
                }

                if (outcome.Kind == EntryKind.InvalidToken)
                {
                    // 令牌过期回登入，不在这里弹「口令错」。
                    Token = string.Empty;
                    _shell.Toast("会话已过期，请重新登入");
                    OpenLogin();
                    return;
                }

                SyncCreateButton();
                SetHint(_createHint, HintOf(outcome.Kind));
            });
        }

        // ------------------------------------------------------------------ 角色栏与载入

        /// <summary>角色栏把「进入所选角色」的按钮交给流程。</summary>
        public void BindRoster(UiButton enter)
        {
            _enterButton = enter;
            if (_enterButton != null)
            {
                _enterButton.Enabled = Character.Ok;
                _enterButton.Apply(_shell.Palette);
            }
        }

        /// <summary>从角色栏进入所选角色。没有服务端确认过的角色就不动。</summary>
        public void EnterChosen()
        {
            if (Busy || !Character.Ok)
            {
                return;
            }

            Debug.Log("u2_roster=enter id=" + Character.CharacterId);
            OpenLoading();
        }

        /// <summary>
        /// 服务端说能不能进世界。这是载入屏唯一的出口：进度条到 100% 也不算数。
        /// U4 接上世界入口后由它调这里；本卡只把口子留好并验到。
        /// </summary>
        public void ConfirmServerEntry(bool ok, string reason)
        {
            if (Current != Step.Loading)
            {
                return;
            }

            if (!ok)
            {
                Debug.Log("u2_loading=refused reason=" + (reason ?? string.Empty));
                if (_loading != null)
                {
                    _loading.SetTip("没能接引，稍后再试");
                }

                Entered = false;
                return;
            }

            Entered = true;
            _progress = 1f;
            Debug.Log("u2_loading=entered id=" + Character.CharacterId);
            if (_loading != null)
            {
                _loading.SetProgress(1f);
                _loading.SetTip("已入世");
            }
        }

        // ------------------------------------------------------------------ 每帧

        void Update()
        {
            Tick(Time.unscaledDeltaTime);
        }

        /// <summary>每帧推进。核对里直接调它，进度可控。</summary>
        public void Tick(float deltaSeconds)
        {
            if (_account != null)
            {
                _account.Tick();
            }

            if (_password != null)
            {
                _password.Tick();
            }

            if (_name != null)
            {
                _name.Tick();
            }

            if (Current != Step.Loading || Entered || _loading == null)
            {
                return;
            }

            // 进度只作表现：自己最多走到 92%，剩下那截等服务端确认。
            if (_progress < 0.92f)
            {
                _progress = Mathf.Min(0.92f, _progress + deltaSeconds * 0.35f);
                _loading.SetProgress(_progress);
            }
        }

        // ------------------------------------------------------------------ 发送

        void Send(string path, string body, string token, Action<int, string> done)
        {
            Requests++;
            if (_send == null)
            {
                done(0, string.Empty);
                return;
            }

            _send(path, body, token, done);
        }

        void SendByUnityWebRequest(string path, string body, string token, Action<int, string> done)
        {
            StartCoroutine(SendRoutine(path, body, token, done));
        }

        IEnumerator SendRoutine(string path, string body, string token, Action<int, string> done)
        {
            var url = Gateway + path;
            using (var request = new UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
                if (!string.IsNullOrEmpty(token))
                {
                    request.SetRequestHeader("Authorization", "Bearer " + token);
                }

                request.timeout = 8;
                yield return request.SendWebRequest();

                var status = (int)request.responseCode;
                var text = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
                if (status == 0)
                {
                    // 连接被拒、超时、DNS 都走这里：不是 HTTP 状态，按「连不上网关」算。
                    Debug.Log("u2_http=fail path=" + path + " result=" + request.result);
                    done(0, string.Empty);
                    yield break;
                }

                done(status, text);
            }
        }

        // ------------------------------------------------------------------ 文案

        static void SetHint(Text hint, string text)
        {
            if (hint != null)
            {
                hint.text = text;
            }
        }

        /// <summary>
        /// 四档失败在屏上的说法。三条必须分得开：口令不对、连不上、暂时连不上。
        /// 文案照原型 `HINTS` 抄，别在屏里另写一份。
        /// </summary>
        public static string HintOf(EntryKind kind)
        {
            switch (kind)
            {
                case EntryKind.BadRequest:
                    return "请求没发出去，重试一次";
                case EntryKind.InvalidCredentials:
                    return "账号或口令不对";
                case EntryKind.InvalidToken:
                    return "会话已过期，请重新登入";
                case EntryKind.NameTaken:
                    return "这个名号已经有人用了";
                case EntryKind.Unavailable:
                    return "暂时连不上，稍后再试";
                case EntryKind.Network:
                    return "连不上网关";
                case EntryKind.Ok:
                    return string.Empty;
                default:
                    return "没成，稍后再试";
            }
        }
    }
}
