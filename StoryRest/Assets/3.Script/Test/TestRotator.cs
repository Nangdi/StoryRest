using UnityEngine;

public class TestRotator : MonoBehaviour
{
    public Vector3 rotateAxis = new Vector3(1f, 1f, 0f);
    public float speed = 90f;

    void Update()
    {
        transform.Rotate(rotateAxis.normalized, speed * Time.deltaTime, Space.Self);
    }
}
