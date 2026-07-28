using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }
    public event Action OnGameStart; // 게임 시작 이벤트
    public event Action OnGameEnd; // 게임 시작 이벤트

    public float gameTimeScale = 1f; // 게임 시간의 흐름을 조절하는 변수
    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }
    private void OnDisable()
    {
        // 도메인 리로드/종료 시점엔 GameTimer가 먼저 파괴돼 Instance가 null일 수 있다.
        if (GameTimer.Instance != null)
            GameTimer.Instance.OnTimeOver -= GameManager_OnGameEnd;
    }
    void Start()
    {
        Time.timeScale = gameTimeScale; // 게임 시작 시 시간 흐름을 설정
        GameTimer.Instance.OnTimeOver += GameManager_OnGameEnd;
    }
    //시간종료시 게임 종료 이벤트 호출
    private void GameManager_OnGameEnd()
    {
        OnGameEnd?.Invoke(); // 게임 종료 이벤트 호출
    }

    // Update is called once per frame
    void Update()
    {
        Time.timeScale = gameTimeScale; // 게임 시작 시 시간 흐름을 설정
        if(Input.GetKeyDown(KeyCode.S))
        {
            OnGameStart?.Invoke(); // 게임 시작 이벤트 호출
        }
        if(Input.GetKeyDown(KeyCode.E))
        {
            OnGameEnd?.Invoke(); // 게임 종료 이벤트 호출
        }
    }
}
