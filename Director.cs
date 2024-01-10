using System.Collections;
using P1SModule.BMSound;
using System.Collections.Generic;
using UnityEngine;
using Facebook.Unity;
using BitMango;
using System;
using System.IO;
using Alpha;
using P1SModule.Alpaca;
using BitMango.Diagnostics;
using Cysharp.Threading.Tasks;
using Data;
using Firebase.Analytics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using P1SModule.BMConsent;
using P1SModule.CheatPad;
using P1SModule.CheatPadTimeUtilityExtension;
using P1SModule.CommonLog;
using P1SModule.Core;
using P1SModule.ExchangeReward;
using P1SModule.QuestBook;
using P1SModule.Security;
using P1SModule.ServerEntry;
using P1SModule.TimeUtility;
using P1SModule.Utility;
using P1SPlatform.Zeus;
using P1SPlatform.Decalcomanie;
using P1SPlatform.AnalyticsBase;
using P1SPlatform.Band;
using P1SPlatform.Band.Prefab;
using P1SPlatform.Billboard.Template;
using P1SPlatform.ForbesRace.Template;
using P1SPlatform.Store;
using Constants = P1SPlatform.AnalyticsBase.Constants;
using FadeTransition = P1SModule.Alpaca.FadeTransition;
using Profile = BitMango.Profile;
using TimerManager = P1SModule.TimeUtility.TimerManager;

public class Director : SingletonMonoBehaviour<Director> {
    public static TextMeshEx textMeshEx;
    public static int unlockRocketItem;
    public static int unlockBombItem;
    public static int unlockLightningItem;
    public static int unlockRainbowItem;
    public static int interstitialCount = 0;
    static bool sendTokenToAppsFlyer = false;
    public static int poorCoin = 100;

    bool bPaused = false;
    
    public static bool IsDirectorInit { get; private set; }
    
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    private static void SetZeusCallback() {
        Debug.Log("------ ZeusHelper :: add Event to ZeusHelper.OnCompleteInit");
        ZeusHelper.OnCompleteInit += OnCompleteInitZeus;
        ZeusHelper.OnSessionExpired += OnSessionExpired;
    }
    
    private static async void OnSessionExpired() {
        Debug.Log("------ ZeusHelper :: SessionExpired !!!!!!!!!!!");
        await SessionExpiredHelper.Show();
    }
    
    private static async void OnCompleteInitZeus(P1SPlatform.Zeus.LoginResult result, LoginBridge.TYPE type) {
        #if UNITY_EDITOR
        if (UnityEditor.EditorPrefs.GetBool("EditorMode")) {
            return;
        }
        #endif
        
        Debugger.Log("------ ZeusHelper :: complete init");
        await UniTask.WaitUntil(() => IsDirectorInit);
        
        Debug.Log($"-------- ZeusHelper :: OnCompleteInit result : {result}");
        var syncResult = await DecalcomanieHelper.SyncWithInitLoginResult(result, type);
        
        Debugger.Log($"ZeusHelper :: syncResult : {syncResult}");
        
        if (syncResult != SyncResult.Fail) {
            // NOTE @hanwoong 이전 firebase sync id 가 남아있는 유저는 로그 전송
            var legacySyncId = PlayerPrefs.GetString("DSH_SYNC_ID", string.Empty);
            if (!string.IsNullOrEmpty(legacySyncId)) {
                var loginBridge = ZeusHelper.GetLoginBridge(ZeusHelper.LoggedInType);
                var uniqueId = loginBridge != null ? loginBridge.UniqueId : string.Empty; 
                AnalyticsHelper.LogEvent("DataSync", ZeusHelper.UserId, uniqueId, legacySyncId);
            }
            
            // HearRequest 쿨타임 계산용 타이머 초기화
            // https://api.bitmango.com/api/time.php 에서 서버시간 가져옴
            TimerManager.Instance.Initialize(new TimerDatasource());

            var band = new BandCustom();
            TeamDBController.Instance.Initialize(band);
            
            LeaderboardDataHelper.UploadPlayerData();

            CheckPremiumpass();
            TreasureHuntManager.CheckSetTargetValid();
        }
        else {
            Debugger.Log($"Zeus syncResult : {syncResult}");
        }
        
        Debug.Log($"--------------- Zeus userId : {ZeusHelper.UserId}");
    }

    public override void Init() {
        Initialize();
    }

    private async UniTask Initialize() {
        Debugger.Log($"Director :: Initialize");

        DontDestroyOnLoad (this.gameObject);
#if USE_Facebook
        if (FB.IsInitialized) {
            FB.ActivateApp();
        } else {
            FB.Init(delegate {
                FB.ActivateApp();
                var eventSystems = FindObjectsOfType<UnityEngine.EventSystems.EventSystem>();
                foreach (var eventSystem in eventSystems) {
                    var tmp = new GameObject();
                    eventSystem.gameObject.transform.parent = tmp.transform;
                }
            });

        } 
#endif
        
        Log.CallVerbose(this, "Facebook.Initialize Complete");
        
        textMeshEx = gameObject.AddComponent<TextMeshEx>();
        
        SetUnlockItem();
        RegisterAdManagerCallback();
        SetCoordinate();
        InitializeUncleBillLogger();
        
        Log.CallVerbose(this, "UncleBill.Initialize Complete");
        
        SeasonEventManager.ResetHoliday();
        Log.CallVerbose(this, "SeasonEventManager.Initialize Complete");

        ActionEventManager.SetCoinInventoryId("coin");
        Log.CallVerbose(this, "ActionEventManager.Initialize Complete");

        TutorialInfo.Initialize();
        Log.CallVerbose(this, "TutorialInfo.Initialize Complete");

        CrownLeagueManager.Instance.Initialize();
        Log.CallVerbose(this, "CrownLeagueManager.Initialize Complete");

        AdventureEventManager.Instance.Init();
        Log.CallVerbose(this, "AdventureEventManager.Initialize Complete");

        CustomQuestManager.Init();
        Log.CallVerbose(this, "CustomQuestManager.Initialize Complete");
        
        P1SPlatform.Store.Wallet.itemChangedCallback += OnChangedItem;
        P1SPlatform.Store.Wallet.itemChangedCallback += OnChangedItemForSync;

        AdManager.Instance.OnClosed += OnCloseInterstitial;

#if USE_AppsFlyer && UNITY_IOS
#if !USE_LocalNotification
        using NotificationServices = UnityEngine.iOS.NotificationServices;
        using NotificationType = UnityEngine.iOS.NotificationType;
        UnityEngine.iOS.NotificationServices.RegisterForNotifications(UnityEngine.iOS.NotificationType.Alert | UnityEngine.iOS.NotificationType.Badge | UnityEngine.iOS.NotificationType.Sound);
#endif
        StartCoroutine("CoRegisterTokenToAppsFlyer");
#endif

        if (P1SPlatform.Store.UncleBill.IsRich == false) {
            AdManager.Instance.Run(AdManagerMode.Interstitial | AdManagerMode.Video);
            if (PlatformContext.Instance.isFirstRun) {
                AdManager.Instance.SkipInterstitial(180f);
            }
            else {
                AdManager.Instance.SkipInterstitial(20f);
            }
        } else {
            AdManager.Instance.Stop();
        }
        
        Log.CallVerbose(this, "AdManager.SkipInterstitial Complete");

        if (PlatformContext.Instance.isFirstRun) {
            PlayerPrefs.SetString("first_run_time", TimeHelper.NowUtc.ToString());
            PlayerPrefs.SetInt("user_playday_count", 0);
            PlayerPrefs.Save();

            var initialCoinCount = 200;
            Wallet.Gain(Common.ITEM_COIN, initialCoinCount);
            UncleBillLogger.InventoryLog(UncleBillStatus.EARN, Common.UNCLEBILL_WHERE.initial.ToString(), Common.ITEM_COIN, initialCoinCount);
        }
        
        Log.CallVerbose(this, "Wallet.Initialize Complete");

        // 유저 정보관련 저장하는부분 UserInfoManager로 이동
        UserInfoManager.RefreshUserInfo();
        
        Log.CallVerbose(this, "UserInfoManager.Initialize Complete");
        
        SolverManager.Instance.SetSolverHandler(new CustomSolverHandler(LevelDataManager.Instance.MaxLevelNumber));
        
        Log.CallVerbose(this, "SolverManager.Initialize Complete");

        Debugger.Log($"Director :: Wait to Firebase Initializer");
        
        if (Application.internetReachability != NetworkReachability.NotReachable) {
            try {
                await UniTask.WaitUntil(() => FirebaseInitializer.CheckAndFixedFirebase).Timeout(TimeSpan.FromSeconds(3f));
                if (FirebaseInitializer.CheckAndFixedFirebase) {
                    LeagueFirebaseRanking.Initialize();
                    DecalcomanieHelper.UploadClientData(DataObjectKey.PREMIUMPASS_DATA);
                    DecalcomanieHelper.UploadClientData(DataObjectKey.CROWNLEAGUE_DATA);
                    
                    Log.CallVerbose(this, "DecalcomanieHelper.UploadData Complete");
                }
            }
            catch (TimeoutException) {
                Debug.LogError("FirebaseInitializer.CheckAndFixedFirebase timeout!!");
            }
        }
        
        Debugger.Log($"Director :: Init complete Firebase Initializer");
        
        IsDirectorInit = true;
    }

    protected override void Awake () {
        // Director 의 초기화 작업은 Initialize 로 통합했기 때문에 base.Awake() 를 호출하지 않음
    }

    public void LazyInitialize() {
        if (Define.IsDevelop || Profile.IsEnableCheat) {
            ServerTime.Instance.IsCheater = true;
            CheatPadManager.Instance.AddCheatPadView(new TimeUtilityCreator(), nameof(LevelPage));
        }
    }
    
    private void RegisterAdManagerCallback() {
        AdManager.Instance.OnPrevShow += OnPrevShow;
        AdManager.Instance.OnClosed += OnClosed;
    }

    private static void OnPrevShow(AdManager.AdType adType) {
        if (adType == AdManager.AdType.INTERSTITIAL) {
            AutoIndicator.Show(true, PlatformContext.Instance.appDisplayName);
        }
    }
    public void OnClosed(AdManager.AdType adType) {
        if (adType == AdManager.AdType.INTERSTITIAL) {
            AutoIndicator.Hide();
        }
    }


    public static void SetUnlockItem() {
      
        unlockRocketItem = 11;
        unlockBombItem = 4;
        unlockLightningItem = 19;
        unlockRainbowItem = 14;
        
    }
    
 #if USE_Firebase
    public void SendFirebaseLog(string message) {
        StartCoroutine(CoWaitFireBaseInit(()=>{
            FirebaseAnalytics.LogEvent(message);
        }));
    }

    public void SendFirebaseLog(string message, string parameterName, int parameterValue) {
        StartCoroutine(CoWaitFireBaseInit(()=>{
            FirebaseAnalytics.LogEvent(message, parameterName, parameterValue);
        }));
    }
    
    public void SendFirebaseUserProperty(string name, string property) {
        StartCoroutine(CoWaitFireBaseInit(()=>{
            FirebaseAnalytics.SetUserProperty(name, property);
        }));
    }
    IEnumerator CoWaitFireBaseInit(System.Action func) {
        while (FirebaseInitializer.FinishedCheckFirebase == false) {
            yield return null;
        }
        if (FirebaseInitializer.CheckAndFixedFirebase == true) {
            func();
        }
    }
#endif   
#if USE_AppsFlyer && UNITY_IOS
    IEnumerator CoRegisterTokenToAppsFlyer() {
        while (!sendTokenToAppsFlyer) {
            Debugger.Log("Registering Token to AppsFlyer...");
            if (Application.internetReachability == NetworkReachability.NotReachable) {
                yield return new WaitForSeconds(10f);
            }

            byte[] token = UnityEngine.iOS.NotificationServices.deviceToken;
            if (token != null) {
                sendTokenToAppsFlyer = true;
            } else {
                yield return new WaitForSeconds(5f);
            }
        }
    }
#endif
    private static void InitializeUncleBillLogger() {
        UncleBillLogger.SetValue("coin", 0.013f);
        UncleBillLogger.SetValue("bomb", 1.327f);
        UncleBillLogger.SetValue("rainbow",  0.884f);
        UncleBillLogger.SetValue("lightning", 1.327f);
        UncleBillLogger.SetValue("rocket", 1.769f);
        
        if (string.IsNullOrEmpty(UncleBillStage.StageName)) {
            // Init StageName (first install)
            LevelDataManager.Instance.LoadLevel(1).ContinueWith(lv => {
                UncleBillStage.SetStageName($"{LevelInfoManager.GetFullLevelPathByLevel(1, lv.author)}");
            });
        }
    }
    
    private void OnChangedItemForSync(InventoryItem item, int count) {
        DecalcomanieHelper.UploadClientData(DataObjectKey.WALLET_DATA);
    }

    void OnChangedItem(P1SPlatform.Store.InventoryItem item, int count) {
       
        if (P1SPlatform.Store.UncleBill.IsRich) {
            AdManager.Instance.Stop();
        }

        if (RootDataHelper.Data.userdata.record.isFirstCoinPoor && item.id.Equals("coin") && count < 0 && P1SPlatform.Store.Wallet.GetItemCount("coin") < poorCoin) {
            RootDataHelper.Data.userdata.record.isFirstCoinPoor = false;
            RootDataHelper.Data.userdata.SaveContext();
        }
        
        if (RootDataHelper.Data.userdata.record.isFirstUseCoin && item.id.Equals("coin") && count < 0) {
            RootDataHelper.Data.userdata.record.isFirstUseCoin = false;
            RootDataHelper.Data.userdata.SaveContext();
        }

        if (UserInfoManager.GetDayVersionScore(BitMango.Profile.InstallVersion) >= UserInfoManager.GetDayVersionScore("20.1015.00")) {
            if (LevelInfoManager.GetNextLevelNumber()-1 > unlockBombItem && item.id == Common.ITEM_BOMB && count > 0) { // bomb unlock 후 처음 bomb을 얻는 경우

                int bomblog = PlayerPrefs.GetInt("FIREBASE_BOMB_LOG", 0);

                if (bomblog == 0) {
#if USE_Firebase
                    Director.Instance.SendFirebaseLog("first_bomb");
#endif
                    PlayerPrefs.SetInt("FIREBASE_BOMB_LOG", 1);
                    PlayerPrefs.Save();
                }
            }
        }
        
        HandlePackageItem(item, count);
   }
   
   // 패키지 상품을 푸시로 받았을때 처리
   private static void HandlePackageItem(InventoryItem item, int count) {
       if (item.id.EndsWith("_pkg")) {
           while (Wallet.GetItemCount(item.id) > 0) {
               var shopItemId = item.id.Substring(0, item.id.Length - 4); // _pkg 제거
//                Debug.Log("shopItem: " + shopItemId);
               var shopItem = UncleBillContext.Instance.GetShopItemByID(shopItemId);
               if (shopItem != null) {
                   foreach (var reward in shopItem.rewards) {
                       Wallet.Gain(reward.itemId, reward.count, "PUSH_NOTIFICATION");
                   }

                   Wallet.Use(item.id, 1, "PUSH_NOTIFICATION"); // _pkg 아이템은 제거한다.
               }
           }

           Wallet.Save();
       }

       if (item.id == ControlCenterHandler.LEVEL_CONTROL) {
           ControlCenterHandler.LevelControl(count);
       }
       
       
   }

   void OnApplicationPause(bool paused) {
       Log.CallVerbose(this, $"OnApplicationPause paused : {paused}");
        if (paused) {
            BubblePopAlarm.RegisterAlarm();
            bPaused = true;
        } else {
            if (bPaused) {
                bPaused = false;
                
                Log.CallVerbose(this, "CheckPremiumpass Start");
                CheckPremiumpass();
                Log.CallVerbose(this, "CheckPremiumpass Complete");
            }
        }
    }

    void OnApplicationQuit() {
        BubblePopAlarm.RegisterAlarm();

    }

    void OnCloseInterstitial(AdManager.AdType adType)
    {
        if (adType != AdManager.AdType.INTERSTITIAL) return;
        if (RootDataHelper.Data.userdata.record.dontShowAdsFree) return;
        interstitialCount++;

        if (interstitialCount == 2 || interstitialCount == 5)
        {
            CustomPopupManager.Show("AdsFreePopup");
        }
    }


    public static void CheckPremiumpass() {
        if (UserLevelData.IsClear(1, 9) == true 
            && BubblePassManager.State != BubblePassManager.PASS_STATE.NONE 
            && BubblePassManager.State != BubblePassManager.PASS_STATE.END 
            && BubblePassManager.LastSeason >= BubblePassManager.originSeason 
            && (BubblePassManager.LastSeason <  BubblePassManager.CurrentSeason
                || (BubblePassManager.LastSeason == BubblePassManager.CurrentSeason && !BubblePassManager.IsBubblePassSeason()))) {
            BubblePassManager.State = BubblePassManager.PASS_STATE.END_POPUP;
        }

        if (UserLevelData.IsClear(1, 9) == true
            && BubblePassManager.IsBubblePassSeason() && BubblePassManager.State == BubblePassManager.PASS_STATE.END) {
            BubblePassManager.State = BubblePassManager.PASS_STATE.NONE;
            BubblePassManager.Reset();
        }
        
        DecalcomanieHelper.UploadClientData(DataObjectKey.PREMIUMPASS_DATA);
    }

    void SetCoordinate(){
        Vector2 screenSize = GetScreenSize();
        float size = (screenSize.x / Coordinate.width) / Coordinate.ORIGIN_SIZE_X;
      
        if (size * Coordinate.ORIGIN_SIZE_Y * 16f > (screenSize.y - (4f * Coordinate.ORIGIN_SIZE_X))) {
            size = (screenSize.y - (4f * Coordinate.ORIGIN_SIZE_X)) / (Coordinate.ORIGIN_SIZE_Y * Coordinate.height);
        }
        Coordinate.scale = size;
        Coordinate.sizeX = 1f * size * Coordinate.ORIGIN_SIZE_X;
        Coordinate.sizeY = 0.866f * size * Coordinate.ORIGIN_SIZE_X;
    }

    public static Vector2 GetScreenSize() {
        Vector3 minPoint = Camera.main.ViewportToWorldPoint(new Vector3(0, 0, Camera.main.nearClipPlane));
        Vector3 maxPoint = Camera.main.ViewportToWorldPoint(new Vector3(1, 1, Camera.main.nearClipPlane));
        // return new Vector2((maxPoint.x - minPoint.x) * 1.76056338028169f, (maxPoint.y - minPoint.y) * 1.76056338028169f);
         return new Vector2((maxPoint.x - minPoint.x), (maxPoint.y - minPoint.y) );
    }

    public static int GetVersionScore (string versionText) {
        int[] results = new int[3];
        string[] texts = versionText.Split('.');
        int versionScore = -1;
        if (texts.Length >= 3 && int.TryParse(texts[0], out results[0]) && int.TryParse(texts[1], out results[1]) && int.TryParse(texts[2], out results[2])) {
            versionScore = results[0]*10000 + results[1]*100 + results[2]; //ex)1.2.63 -- 2자리로 쓸수도 있어서...
        }
        return versionScore;
    }

    public static void CountFirebaseReward() {
        string rewardCountDay = PlayerPrefs.GetString("FIREBASE_REWARD_COUNT_DAY", "");
        if (rewardCountDay == TimeHelper.Today.ToString("yyyy/MM/dd")) {
            int count = PlayerPrefs.GetInt("FIREBASE_REWARD_COUNT", 5);
            if (count > 0) {
                count--;
                PlayerPrefs.SetInt("FIREBASE_REWARD_COUNT", count);
                PlayerPrefs.Save();
                if (count == 0) {
#if USE_Firebase
                    Director.Instance.SendFirebaseLog("reward5_complete");
#endif
                }
            }
        }
    }
}
