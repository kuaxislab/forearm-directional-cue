using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.InputSystem;
using System.Collections;
using System;
using System.Text; // Encoding을 위해 필요

public class NetworkTester : MonoBehaviour
{
    public string testUrl = "http://10.17.176.153:8000/api/direction"; 

    void Update()
    {
        if (Keyboard.current != null && Keyboard.current.tKey.wasPressedThisFrame)
        {
            Debug.Log("'T' 키 입력 감지! JSON 데이터를 전송합니다.");
            StartCoroutine(SendJsonTest());
        }
    }

    IEnumerator SendJsonTest()
    {
        // 1. 데이터를 JSON 객체로 생성
        var data = new MovementData {
            direction = "NW",
            currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            timeSinceCooldown = 1.23f
        };

        string jsonPayload = JsonUtility.ToJson(data);
        byte[] jsonToSend = Encoding.UTF8.GetBytes(jsonPayload);

        // 2. UnityWebRequest 설정
        using (UnityWebRequest www = new UnityWebRequest(testUrl, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(jsonToSend);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                Debug.Log("<color=green>통신 성공!</color> 서버 응답: " + www.downloadHandler.text);
            }
            else
            {
                // 400 에러 시 서버가 보낸 구체적인 에러 메시지 확인
                Debug.LogError($"<color=red>통신 실패:</color> {www.responseCode} {www.error}\n응답내용: {www.downloadHandler.text}");
            }
        }
    }

    // JSON 변환을 위한 클래스
    [Serializable]
    public class MovementData {
        public string direction;
        public string currentTime;
        public float timeSinceCooldown;
    }
}