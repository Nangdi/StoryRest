using System;
using UnityEngine;

public class GameTimer : MonoBehaviour
{
    private static GameTimer instance;

    // 다른 스크립트가 처음 접근하는 시점에 씬에서 한 번 찾아서 보완하는 lazy singleton getter입니다.
    public static GameTimer Instance
    {
        get
        {
            if (instance == null)
                instance = FindObjectOfType<GameTimer>();

            return instance;
        }
        private set
        {
            instance = value;
        }
    }
    public bool IsRunning { get; private set; }
    [SerializeField] private float currentTime;

    [Header("Time Range")]
    public float gameTime = 900f; // 15분 = 900초
    public float rePlayTime = 60f; // 1분 = 60초
    public float targetTime = 300f;
    public bool isRePlay = false;
    public float CurrentTime
    {
        get { return currentTime; }
        private set { currentTime = value; }
    }

    public event Action<float> OnTimeChanged;
    public event Action OnTimeOver;
    public event Action OnReplayEnd;
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }
    private void OnDestroy()
    {
        // 현재 싱글톤이 파괴될 때는 정적 참조도 같이 비워서 다음 탐색이 가능하게 합니다.
        if (Instance == this)
            Instance = null;
    }
    private void Start()
    {
        GameManager.Instance.OnGameStart += Timer_OnGameStart;
        GameManager.Instance.OnGameEnd += Timer_OnGameEnd;
    }
    private void Update()
    {
        if (!IsRunning) return;

        CurrentTime += Time.deltaTime;
        OnTimeChanged?.Invoke(currentTime);

      
        if (CurrentTime >= targetTime)
        {
           
            //게임매니저에 게임종료 알림
            Debug.Log("Time Over!");
            OnTimeOver?.Invoke();
        }
    }
    public void StartTimer()
    {
    
        CurrentTime = 0f;
        IsRunning = true;
    }
   
    public void StopTimer()
    {
        IsRunning = false;
    }

    public void ResetTimer()
    {
        CurrentTime = 0f;
        OnTimeChanged?.Invoke(currentTime);
    }
    public void SetGameTime(float time)
    {
        gameTime = time;
    }


    public float GetCurrentTime()
    {
        return CurrentTime;
    }
    private void Timer_OnGameStart()
    {
        StartTimer();
    }
    private void Timer_OnGameEnd()
    {
        ResetTimer();
        StopTimer();
    }
    
}