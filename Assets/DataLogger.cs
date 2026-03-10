using UnityEngine;
using System.IO;
using System.Text;
using System;

public class DataLogger : MonoBehaviour
{
    [Header("Logging Settings")]
    public Transform player;         
    public float logInterval = 0.1f; 
    
    private string filePath;
    private StringBuilder csvContent = new StringBuilder();
    private float nextLogTime = 0f;
    private bool isLogging = true;
    private PointManager pointManager;

    void Awake() { pointManager = GetComponent<PointManager>(); }

    void Start()
    {
        string folderPath = Path.Combine(Application.dataPath, "ExperimentData");
        if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

        string fileName = "Exp_Data_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv";
        filePath = Path.Combine(folderPath, fileName);

        csvContent.AppendLine("Timestamp,State,TargetIndex,PosX,PosZ,RotY,DistanceToTarget,EventDetails");
        Debug.Log($"[DataLogger] Ready: {filePath}");
    }

    void Update()
    {
        if (!isLogging || player == null || pointManager == null) return;
        if (Time.time >= nextLogTime) {
            AppendRow("TRACK", "-");
            nextLogTime = Time.time + logInterval;
        }
    }

    public void LogEvent(string detail) { if (isLogging) AppendRow("EVENT", detail); }

    private void AppendRow(string state, string detail)
    {
        float time = Time.time;
        int targetIdx = pointManager.GetCurrentTargetIndex() + 1;
        float px = player.position.x; float pz = player.position.z;
        float ry = player.eulerAngles.y; float dist = pointManager.GetDistanceToTarget();
        csvContent.AppendLine($"{time:F2},{state},{targetIdx},{px:F2},{pz:F2},{ry:F2},{dist:F2},{detail}");
    }

    public void SaveAndStop()
    {
        if (!isLogging) return;
        isLogging = false;
        File.WriteAllText(filePath, csvContent.ToString());
#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    private void OnApplicationQuit() { SaveAndStop(); }
}