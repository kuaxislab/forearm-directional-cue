using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using System;

public class GridMovement : MonoBehaviour
{
    [Header("Input Settings")]
    public InputActionReference moveAction;
    public InputActionReference clickAction;

    [Header("Visual Settings")]
    public GameObject arrowIndicator;

    [Header("Movement Settings")]
    public float moveDuration = 1.0f;
    public float moveCooldown = 1.5f;

    private float gridSize = 5f;

    // PointManager에서 이동 완료 시점을 정확히 감지하기 위한 이벤트
    public event Action OnMoveCompleted;

    // PointManager에서 텔레포트 대기에 사용
    public bool IsMoving { get; private set; } = false;

    private float lastMoveTime = -999f;
    private const float deadzone = 0.5f;

    private Vector3 currentAimDirection = Vector3.zero;
    public bool isFirstInputWaiting = true;

    void Start()
    {
        if (arrowIndicator != null)
            arrowIndicator.SetActive(false);
    }

    void OnEnable()
    {
        if (moveAction != null) moveAction.action.Enable();
        if (clickAction != null) clickAction.action.Enable();
    }

    void OnDisable()
    {
        if (moveAction != null) moveAction.action.Disable();
        if (clickAction != null) clickAction.action.Disable();
    }

    void Update()
    {
        if (IsMoving) return;

        HandleAiming();

        if (clickAction != null && clickAction.action.WasPressedThisFrame())
            HandleMovementExecution();
    }

    void HandleAiming()
    {
        if (moveAction == null) return;

        Vector2 input = moveAction.action.ReadValue<Vector2>();

        if (input.magnitude > deadzone)
        {
            float angle = Mathf.Atan2(input.x, input.y) * Mathf.Rad2Deg;
            float snappedAngle = Mathf.Round(angle / 45f) * 45f;
            currentAimDirection = Quaternion.Euler(0, snappedAngle, 0) * Vector3.forward;

            if (arrowIndicator != null)
            {
                if (!arrowIndicator.activeSelf) arrowIndicator.SetActive(true);
                arrowIndicator.transform.rotation = Quaternion.Euler(90, snappedAngle, 0);
            }
        }
    }

    void HandleMovementExecution()
    {
        if (currentAimDirection == Vector3.zero) return;

        if (isFirstInputWaiting)
        {
            isFirstInputWaiting = false;

            PointManager pm = FindObjectOfType<PointManager>();
            if (pm != null) pm.TriggerInitialVibration();

            if (arrowIndicator != null) arrowIndicator.SetActive(false);
            currentAimDirection = Vector3.zero;
            lastMoveTime = Time.time;
            return;
        }

        if (Time.time < lastMoveTime + moveCooldown) return;

        Vector3 currentGridPos = new Vector3(
            Mathf.Round(transform.position.x / gridSize) * gridSize,
            transform.position.y,
            Mathf.Round(transform.position.z / gridSize) * gridSize
        );

        float xStep = Mathf.Round(currentAimDirection.x);
        float zStep = Mathf.Round(currentAimDirection.z);
        Vector3 targetPos = currentGridPos + new Vector3(xStep, 0, zStep) * gridSize;

        StartCoroutine(MoveToGrid(targetPos));
        lastMoveTime = Time.time;

        if (arrowIndicator != null) arrowIndicator.SetActive(false);
        currentAimDirection = Vector3.zero;
    }

    IEnumerator MoveToGrid(Vector3 target)
    {
        IsMoving = true;

        Animator anim = GetComponentInChildren<Animator>();
        if (anim != null)
        {
            anim.SetFloat("InputX", currentAimDirection.x);
            anim.SetFloat("InputZ", currentAimDirection.z);
            anim.SetBool("isWalking", true);
        }

        Vector3 startPos = transform.position;
        float elapsed = 0f;

        while (elapsed < moveDuration)
        {
            transform.position = Vector3.Lerp(startPos, target, elapsed / moveDuration);
            elapsed += Time.deltaTime;
            yield return null;
        }

        // 정확한 그리드 위치로 강제 보정
        transform.position = new Vector3(
            Mathf.Round(target.x / 5f) * 5f,
            target.y,
            Mathf.Round(target.z / 5f) * 5f
        );

        if (anim != null)
        {
            anim.SetFloat("InputX", 0);
            anim.SetFloat("InputZ", 0);
            anim.SetBool("isWalking", false);
        }

        IsMoving = false;

        // 이동이 완전히 끝난 후 이벤트 발생 → 카운트는 항상 정확히 1회
        OnMoveCompleted?.Invoke();
    }
}