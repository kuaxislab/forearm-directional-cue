using UnityEngine;
using TMP_Text = TMPro.TMP_Text;
using System.Collections.Generic;
using System.Collections;
using UnityEngine.Networking;
using System;
using System.Text;
using System.IO;
using Random = UnityEngine.Random;

[Serializable]
public class MapData
{
    public int map_id;
    public int map_seed;
    public List<Vector3> target_points = new List<Vector3>();
}

[Serializable]
public class MovementData
{
    public string direction;
    public string currentTime;
    public float timeSinceCooldown;
}

[Serializable]
public class TrialLogEntry
{
    public string timestamp;
    public int point_index;
    public int attempt_index;
    public string correct_direction;
    public string responded_direction;
    public string status;
}

[Serializable]
public class TrialLogCollection
{
    public List<TrialLogEntry> logs = new List<TrialLogEntry>();
}

public class PointManager : MonoBehaviour
{
    [Header("Experiment Settings")]
    [Tooltip("피험자 ID (예: 001)")]
    public string subjectId = "001";
    [Tooltip("세션 번호 (예: 1)")]
    public string sessionId = "1";

    [Header("Required UI Objects")]
    public Transform player;
    public TMP_Text directionText;
    public TMP_Text statusText;

    [Header("Map Settings")]
    [Tooltip("체크하면 map 폴더의 json 파일을 불러옵니다. 체크 해제 시 랜덤 생성합니다.")]
    public bool useJsonMap = true;
    [Tooltip("불러올 맵의 인덱스 번호 (0 ~ 9)")]
    public int mapIndexToLoad = 0;

    [Header("Environment: Prefabs")]
    public GameObject grassPrefab;
    public GameObject balloonPrefab;
    public GameObject airplanePrefab;
    public GameObject cloudPrefab;

    [Header("Generation Settings")]
    public int randomSeed = 100;
    public int grassCount = 200;
    public int balloonCount = 15;
    public int cloudCount = 25;
    public float mapSize = 45f;
    public float arrivalThreshold = 2.5f;

    [Header("Path & Failure Settings")]
    public int totalPoints = 8;
    public float pathRadius = 25f;
    public int maxMoveAttempts = 10;
    public float gridStep = 5.0f;

    [Header("Network Settings")]
    public string serverUrl = "http://10.17.176.153:8000/api/direction";
    [Tooltip("API 실패 시 재시도 횟수")]
    public int maxRetryCount = 3;
    [Tooltip("재시도 간격 (초)")]
    public float retryInterval = 1.0f;
    [Tooltip("요청 타임아웃 (초)")]
    public int requestTimeout = 4;

    // --- Private State ---
    private List<Vector3> points = new List<Vector3>();
    private List<GameObject> targetMarkers = new List<GameObject>();
    private int currentPointIndex = 0;
    private int currentMoveCount = 0;
    private int totalMoveCount = 0;
    private string[] directionNames = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    private DataLogger dataLogger;
    private GridMovement gridMovement;
    private bool isProcessingArrival = false;

    private Vector3 previousFramePosition;
    private bool isMoving = false;

    // requestPending = 실제 HTTP 전송 중일 때만 true
    private bool requestPending = false;

    private string csvFilePath;
    private string jsonFilePath;
    private string lastSentDirection = "NONE";
    private TrialLogCollection logCollection = new TrialLogCollection();

    private Coroutine activeVibeRoutine;

    // ─────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────

    void Start()
    {
        InitializeLogger();

        if (useJsonMap)
            LoadMapFromJson(mapIndexToLoad);
        else
        {
            Random.InitState(randomSeed);
            GenerateRandomPoints();
        }

        SpawnEnvironment();

        dataLogger   = GetComponent<DataLogger>();
        gridMovement = player.GetComponent<GridMovement>();

        if (gridMovement != null)
            gridMovement.OnMoveCompleted += HandleMoveCompleted;

        previousFramePosition = player.position;

        if (statusText != null) statusText.text = "Experiment Started: Head to P1";
    }

    void OnDestroy()
    {
        if (gridMovement != null)
            gridMovement.OnMoveCompleted -= HandleMoveCompleted;
    }

    // ─────────────────────────────────────────────
    // Move Completed Event Handler
    // [핵심] 이동 완료 시점에 정확히 1회 전송
    // 쿨다운 없음 — 이동 1회 = 전송 1회 보장
    // ─────────────────────────────────────────────

    void HandleMoveCompleted()
    {
        if (isProcessingArrival) return;
        if (currentPointIndex >= points.Count) return;

        currentMoveCount++;
        totalMoveCount++;
        Debug.Log($"Attempt: {currentMoveCount}/{maxMoveAttempts}  |  Total: {totalMoveCount}");

        UpdateDirectionUI();

        // 이동 완료 시점의 정확한 방향으로 전송
        // 이전 전송이 아직 진행 중이면 완료될 때까지 기다렸다가 전송
        string dir = GetDirectionString(player.position, points[currentPointIndex]);
        lastSentDirection = dir;
        StopActiveVibe();
        activeVibeRoutine = StartCoroutine(SendDirectionDataWithRetry(dir, 0f));

        // 실패 체크
        if (currentMoveCount >= maxMoveAttempts)
        {
            isProcessingArrival = true;
            StartCoroutine(HandleFailureTeleport());
        }
    }

    // ─────────────────────────────────────────────
    // Update
    // ─────────────────────────────────────────────

    void Update()
    {
        if (currentPointIndex >= points.Count || isProcessingArrival) return;

        bool currentlyMoving = Vector3.Distance(player.position, previousFramePosition) > 0.001f;
        Vector3 targetPos = points[currentPointIndex];

        // ── 1. 도착 체크 ──────────────────────────────────────────────────────
        float distToTarget = Vector2.Distance(
            new Vector2(player.position.x, player.position.z),
            new Vector2(targetPos.x, targetPos.z));

        if (distToTarget < arrivalThreshold)
        {
            isProcessingArrival = true;
            StartCoroutine(HandleSuccessArrival());
            return;
        }

        // ── 2. 방향 UI 업데이트 ───────────────────────────────────────────────
        UpdateDirectionUI();

        // ── 3. 이동 시작 시 로그 기록 (전송은 HandleMoveCompleted에서 담당) ──
        if (currentlyMoving && !isMoving)
        {
            string correctDir  = lastSentDirection;
            string responseDir = GetDirectionString(previousFramePosition, player.position);
            LogTrialData(currentPointIndex, currentMoveCount, correctDir, responseDir, "ongoing");
        }

        isMoving = currentlyMoving;
        previousFramePosition = player.position;
    }

    void UpdateDirectionUI()
    {
        if (directionText == null || currentPointIndex >= points.Count) return;
        string dir = GetDirectionString(player.position, points[currentPointIndex]);
        directionText.text =
            $"Target P{currentPointIndex + 1} / P{points.Count}\n" +
            $"<color=yellow><size=150%>{dir}</size></color>\n" +
            $"Attempts: {currentMoveCount} / {maxMoveAttempts}\n" +
            $"<color=white>Total Moves: {totalMoveCount}</color>";
    }

    // ─────────────────────────────────────────────
    // Arrival / Failure Handlers
    // ─────────────────────────────────────────────

    IEnumerator HandleSuccessArrival()
    {
        StopActiveVibe();

        if (dataLogger != null) dataLogger.LogEvent($"P{currentPointIndex + 1}_SUCCESS");
        LogTrialData(currentPointIndex, currentMoveCount, lastSentDirection, "N/A", "success");

        yield return StartCoroutine(ArrivalEffect(currentPointIndex, targetMarkers[currentPointIndex], Color.cyan));

        ResetForNextPoint();
    }

    IEnumerator HandleFailureTeleport()
    {
        StopActiveVibe();
        statusText.text = "<color=red>Max Attempts Reached! Teleporting...</color>";
        LogTrialData(currentPointIndex, currentMoveCount, lastSentDirection, "N/A", "failed");

        if (gridMovement != null)
            yield return new WaitUntil(() => !gridMovement.IsMoving);
        else
            yield return new WaitForSeconds(0.3f);

        Vector3 targetPos = points[currentPointIndex];
        CharacterController cc = player.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = false;

        player.position       = new Vector3(targetPos.x, 0f, targetPos.z);
        previousFramePosition = player.position;
        isMoving = false;

        yield return new WaitForFixedUpdate();
        if (cc != null) cc.enabled = true;

        yield return StartCoroutine(ArrivalEffect(currentPointIndex, targetMarkers[currentPointIndex], Color.red));

        ResetForNextPoint();
    }

    void ResetForNextPoint()
    {
        currentPointIndex++;
        currentMoveCount      = 0;
        previousFramePosition = player.position;
        isMoving = false;

        if (currentPointIndex >= points.Count)
        {
            isProcessingArrival = true;
            directionText.text = "MISSION COMPLETE";
            if (dataLogger != null) dataLogger.SaveAndStop();
        }
        else
        {
            isProcessingArrival = false;
            TriggerInitialVibration();
        }
    }

    // ─────────────────────────────────────────────
    // Vibration / Network
    // ─────────────────────────────────────────────

    public void TriggerInitialVibration()
    {
        if (currentPointIndex >= points.Count) return;

        StopActiveVibe();
        string initialDirection = GetDirectionString(player.position, points[currentPointIndex]);
        lastSentDirection = initialDirection;
        activeVibeRoutine = StartCoroutine(SendDirectionDataWithRetry(initialDirection, 0f));
    }

    // 재시도 래퍼 — 실패 시 retryInterval 간격으로 maxRetryCount 회 재시도
    IEnumerator SendDirectionDataWithRetry(string directionInfo, float cooldown)
    {
        if (mapIndexToLoad == 0)
        {
            Debug.Log("<color=cyan>Map ID is 0. API Request Skipped.</color>");
            yield break;
        }

        // 이전 전송이 진행 중이면 완료될 때까지 대기
        yield return new WaitUntil(() => !requestPending);

        requestPending = true;

        int attempt = 0;
        bool success = false;

        while (attempt <= maxRetryCount && !success)
        {
            if (attempt > 0)
            {
                Debug.LogWarning($"<color=yellow>재시도 {attempt}/{maxRetryCount}...</color>");
                yield return new WaitForSeconds(retryInterval);
            }

            bool[] result = { false };
            yield return StartCoroutine(SendDirectionData(directionInfo, cooldown, result));
            success = result[0];
            attempt++;
        }

        if (!success)
            Debug.LogError($"<color=red>API 최종 실패 (재시도 {maxRetryCount}회 소진): {directionInfo}</color>");

        requestPending = false;
    }

    IEnumerator SendDirectionData(string directionInfo, float cooldown, bool[] resultOut)
    {
        byte[] jsonToSend = Encoding.UTF8.GetBytes(JsonUtility.ToJson(new MovementData
        {
            direction         = directionInfo,
            currentTime       = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            timeSinceCooldown = cooldown
        }));

        using (UnityWebRequest www = new UnityWebRequest(serverUrl, "POST"))
        {
            www.uploadHandler   = new UploadHandlerRaw(jsonToSend);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.timeout = requestTimeout;

            Debug.Log($"<color=green>통신 요청! [{directionInfo}]</color>");
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"<color=green>통신 성공! [{directionInfo}]</color>");
                resultOut[0] = true;
            }
            else
            {
                Debug.LogWarning($"<color=yellow>통신 실패: {www.error} [{directionInfo}]</color>");
                resultOut[0] = false;
            }
        }
    }

    void StopActiveVibe()
    {
        if (activeVibeRoutine != null)
        {
            StopCoroutine(activeVibeRoutine);
            activeVibeRoutine = null;
        }
        requestPending = false;
    }

    // ─────────────────────────────────────────────
    // Arrival Effect
    // ─────────────────────────────────────────────

    IEnumerator ArrivalEffect(int idx, GameObject marker, Color effectColor)
    {
        statusText.text = effectColor == Color.red
            ? "FAILURE: AUTO-MOVED"
            : $"TARGET P{idx + 1} ACQUIRED!";

        if (marker != null)
        {
            MeshRenderer mr = marker.GetComponent<MeshRenderer>();
            mr.enabled = true;
            mr.material.color = effectColor;

            GameObject shockwave = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(shockwave.GetComponent<Collider>());
            shockwave.transform.position   = points[idx];
            shockwave.transform.localScale = new Vector3(0.1f, 0.01f, 0.1f);
            MeshRenderer swRenderer = shockwave.GetComponent<MeshRenderer>();
            swRenderer.material.color = new Color(effectColor.r, effectColor.g, effectColor.b, 0.5f);

            Vector3 swStart = new Vector3(0.1f, 0.01f, 0.1f);
            Vector3 swEnd   = new Vector3(10f,  0.01f, 10f);
            Color   cStart  = new Color(effectColor.r, effectColor.g, effectColor.b, 0.5f);
            Color   cEnd    = new Color(effectColor.r, effectColor.g, effectColor.b, 0f);

            float duration = 1.0f;
            float elapsed  = 0f;

            while (elapsed < duration)
            {
                float t = elapsed / duration;
                marker.transform.Rotate(Vector3.up * 500f * Time.deltaTime);
                marker.transform.position      += Vector3.up * Time.deltaTime * 2f;
                shockwave.transform.localScale  = Vector3.Lerp(swStart, swEnd, t);
                swRenderer.material.color       = Color.Lerp(cStart, cEnd, t);
                elapsed += Time.deltaTime;
                yield return null;
            }

            Destroy(marker);
            Destroy(shockwave);
        }
    }

    // ─────────────────────────────────────────────
    // Logging
    // ─────────────────────────────────────────────

    void InitializeLogger()
    {
        string subjectLogDir = Path.Combine(Application.dataPath, "log", $"S{subjectId}");
        if (!Directory.Exists(subjectLogDir))
            Directory.CreateDirectory(subjectLogDir);

        string fileBase  = $"S{subjectId}_{sessionId}_{DateTime.Now:yyyyMMdd_HHmmss}";
        csvFilePath  = Path.Combine(subjectLogDir, $"{fileBase}.csv");
        jsonFilePath = Path.Combine(subjectLogDir, $"{fileBase}.json");

        File.WriteAllText(csvFilePath,
            "Timestamp,Point_Index,Attempt_Index,Correct_Direction,Responded_Direction,Status\n",
            Encoding.UTF8);
        File.WriteAllText(jsonFilePath,
            JsonUtility.ToJson(logCollection, true),
            Encoding.UTF8);
    }

    void LogTrialData(int pointIdx, int attemptIdx, string correctDir, string responseDir, string status)
    {
        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

        try { File.AppendAllText(csvFilePath, $"{ts},{pointIdx},{attemptIdx},{correctDir},{responseDir},{status}\n", Encoding.UTF8); }
        catch (Exception e) { Debug.LogError($"CSV 기록 실패: {e.Message}"); }

        logCollection.logs.Add(new TrialLogEntry
        {
            timestamp           = ts,
            point_index         = pointIdx,
            attempt_index       = attemptIdx,
            correct_direction   = correctDir,
            responded_direction = responseDir,
            status              = status
        });

        try { File.WriteAllText(jsonFilePath, JsonUtility.ToJson(logCollection, true), Encoding.UTF8); }
        catch (Exception e) { Debug.LogError($"JSON 기록 실패: {e.Message}"); }
    }

    // ─────────────────────────────────────────────
    // Map Generation
    // ─────────────────────────────────────────────

    void LoadMapFromJson(int mapIndex)
    {
        string mapPath = Path.Combine(Application.dataPath, "map", $"map_{mapIndex}.json");

        if (File.Exists(mapPath))
        {
            MapData mapData = JsonUtility.FromJson<MapData>(File.ReadAllText(mapPath, Encoding.UTF8));
            randomSeed = mapData.map_seed;
            Random.InitState(randomSeed);
            points = mapData.target_points;

            for (int i = 0; i < points.Count; i++)
                CreateMarker(points[i], i);

            Debug.Log($"<color=cyan>맵 로드 성공:</color> map_{mapIndex}.json (Seed: {randomSeed})");
        }
        else
        {
            Debug.LogError($"<color=red>맵 파일 없음:</color> {mapPath} → 랜덤 생성으로 대체");
            Random.InitState(randomSeed);
            GenerateRandomPoints();
        }
    }

    void GenerateRandomPoints()
    {
        points.Clear();
        for (int i = 0; i < totalPoints; i++)
        {
            float rx = Mathf.Round(Random.Range(-pathRadius, pathRadius) / gridStep) * gridStep;
            float rz = Mathf.Round(Random.Range(-pathRadius, pathRadius) / gridStep) * gridStep;
            Vector3 newPoint = new Vector3(rx, 0, rz);
            points.Add(newPoint);
            CreateMarker(newPoint, i);
        }
    }

    void CreateMarker(Vector3 pos, int index)
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.transform.position   = pos + Vector3.up * 1.5f;
        marker.transform.localScale = Vector3.one;
        marker.name = "Marker_" + index;
        marker.GetComponent<MeshRenderer>().enabled = false;
        CleanCollider(marker);
        targetMarkers.Add(marker);
    }

    [ContextMenu("Generate 10 Maps (JSON)")]
    public void Generate10MapsToJson()
    {
        string mapDir = Path.Combine(Application.dataPath, "map");
        if (!Directory.Exists(mapDir)) Directory.CreateDirectory(mapDir);

        List<MapData> uniqueMaps = new List<MapData>();
        for (int i = 0; i < 5; i++)
        {
            MapData m = new MapData { map_seed = 1000 + i };
            Random.InitState(m.map_seed);
            for (int p = 0; p < totalPoints; p++)
            {
                float rx = Mathf.Round(Random.Range(-pathRadius, pathRadius) / gridStep) * gridStep;
                float rz = Mathf.Round(Random.Range(-pathRadius, pathRadius) / gridStep) * gridStep;
                m.target_points.Add(new Vector3(rx, 0, rz));
            }
            uniqueMaps.Add(m);
        }

        int[] mapPairings = { 0, 1, 1, 3, 2, 0, 3, 4, 4, 2 };
        for (int i = 0; i < 10; i++)
        {
            MapData src   = uniqueMaps[mapPairings[i]];
            MapData final = new MapData
            {
                map_id        = i,
                map_seed      = src.map_seed,
                target_points = new List<Vector3>(src.target_points)
            };
            File.WriteAllText(
                Path.Combine(mapDir, $"map_{i}.json"),
                JsonUtility.ToJson(final, true),
                Encoding.UTF8);
        }
        Debug.Log("<color=green>10개 맵 생성 완료!</color>");
    }

    // ─────────────────────────────────────────────
    // Environment
    // ─────────────────────────────────────────────

    void SpawnEnvironment()
    {
        for (int i = 0; i < grassCount; i++)
            SpawnObject(grassPrefab, 0f, true, Random.Range(0.8f, 1.3f));

        for (int i = 0; i < balloonCount; i++)
        {
            GameObject b = SpawnObject(balloonPrefab, Random.Range(1.2f, 1.8f), false, 1.0f);
            if (b != null) ApplyRandomColor(b);
        }

        if (airplanePrefab != null)
        {
            GameObject plane = Instantiate(airplanePrefab,
                new Vector3(0, 30f, 20f), Quaternion.Euler(0, 45f, 0));
            CleanCollider(plane);
        }

        SpawnDistributedClouds();
    }

    void SpawnDistributedClouds()
    {
        if (cloudPrefab == null) return;
        int gridSide = Mathf.CeilToInt(Mathf.Sqrt(cloudCount));
        float cellSize = (mapSize * 4f) / gridSide;

        for (int i = 0; i < gridSide; i++)
        for (int j = 0; j < gridSide; j++)
        {
            float xPos = -mapSize * 2f + i * cellSize + Random.Range(0, cellSize);
            float zPos = -mapSize * 2f + j * cellSize + Random.Range(0, cellSize);
            GameObject cloud = Instantiate(cloudPrefab,
                new Vector3(xPos, Random.Range(40f, 60f), zPos), Quaternion.identity);
            cloud.transform.localScale *= Random.Range(5f, 12f);
            CleanCollider(cloud);
        }
    }

    GameObject SpawnObject(GameObject prefab, float yPos, bool randomRotation, float scale)
    {
        if (prefab == null) return null;
        Vector3 pos = new Vector3(
            Random.Range(-mapSize, mapSize), yPos, Random.Range(-mapSize, mapSize));
        GameObject obj = Instantiate(prefab, pos, Quaternion.identity);
        if (randomRotation)
            obj.transform.rotation = Quaternion.Euler(0, Random.Range(0, 360), 0);
        obj.transform.localScale *= scale;
        CleanCollider(obj);
        return obj;
    }

    void ApplyRandomColor(GameObject obj)
    {
        MeshRenderer r = obj.GetComponentInChildren<MeshRenderer>();
        if (r != null) r.material.color = Random.ColorHSV(0f, 1f, 0.8f, 1f, 0.9f, 1f);
    }

    void CleanCollider(GameObject obj)
    {
        foreach (var c in obj.GetComponentsInChildren<Collider>()) Destroy(c);
    }

    // ─────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────

    string GetDirectionString(Vector3 startPos, Vector3 endPos)
    {
        Vector3 dir = (endPos - startPos).normalized;
        if (dir == Vector3.zero) return "NONE";

        float angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        if (angle < 0) angle += 360f;

        int dirIndex = Mathf.RoundToInt(angle / 45f) % 8;
        return directionNames[dirIndex];
    }

    public int GetCurrentTargetIndex() => currentPointIndex;

    public float GetDistanceToTarget()
    {
        if (currentPointIndex >= points.Count) return 0f;
        return Vector2.Distance(
            new Vector2(player.position.x, player.position.z),
            new Vector2(points[currentPointIndex].x, points[currentPointIndex].z));
    }
}