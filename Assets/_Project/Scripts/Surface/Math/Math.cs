using UnityEngine;

namespace Surface.Math
{
    public static class BoundingBox
    {
        public static bool CalculateArmBounds(
            ref Matrix4x4 depthVP, ref Vector3 wristPos, ref Vector3 elbowPos, ref Vector3 camPos, 
            float fov, int pixelWidth, int pixelHeight, float maxRadialDist, float pixelStride,
            out int xMin, out int yMin, out int width, out int height)
        {
            xMin = yMin = width = height = 0;

            float halfWidth = pixelWidth * 0.5f;
            float halfHeight = pixelHeight * 0.5f;

            // Project Wrist
            ProjectPoint(ref depthVP, ref wristPos, halfWidth, halfHeight, out float wristX, out float wristY, out float wristW);
            if (wristW <= 0f) return false;

            // Project Elbow
            ProjectPoint(ref depthVP, ref elbowPos, halfWidth, halfHeight, out float elbowX, out float elbowY, out float elbowW);
            if (elbowW <= 0f) return false;

            // Unrolled distance calculation (avoids Vector3 struct allocation)
            float midX = (wristPos.x + elbowPos.x) * 0.5f - camPos.x;
            float midY = (wristPos.y + elbowPos.y) * 0.5f - camPos.y;
            float midZ = (wristPos.z + elbowPos.z) * 0.5f - camPos.z;
            float armMidDist = Mathf.Sqrt(midX * midX + midY * midY + midZ * midZ);

            // Hardcode Deg2Rad (0.01745329f) to avoid Unity constant lookup
            float focalPx = pixelHeight / (2f * Mathf.Tan(fov * 0.5f * 0.0174532924f));
            float dynamicPadding = (maxRadialDist / armMidDist) * focalPx;

            // Fast inline Min/Max using ternary operators
            float minX = wristX < elbowX ? wristX : elbowX;
            float maxX = wristX > elbowX ? wristX : elbowX;
            float minY = wristY < elbowY ? wristY : elbowY;
            float maxY = wristY > elbowY ? wristY : elbowY;

            float fXMin = minX - dynamicPadding;
            float fXMax = maxX + dynamicPadding;
            float fYMin = minY - dynamicPadding;
            float fYMax = maxY + dynamicPadding;

            // Fast clamp to screen bounds
            fXMin = fXMin > 0f ? fXMin : 0f;
            fXMax = fXMax < pixelWidth ? fXMax : pixelWidth;
            fYMin = fYMin > 0f ? fYMin : 0f;
            fYMax = fYMax < pixelHeight ? fYMax : pixelHeight;

            if (fXMax - fXMin < pixelStride || fYMax - fYMin < pixelStride) return false;

            xMin = (int)fXMin;
            yMin = (int)fYMin;
            width = (int)(fXMax - fXMin);
            height = (int)(fYMax - fYMin);

            return true;
        }

        private static void ProjectPoint(ref Matrix4x4 vp, ref Vector3 pos, float halfW, float halfH, out float pxX, out float pxY, out float w)
        {
            // Calculate W first to check if the point is behind the camera
            w = vp.m30 * pos.x + vp.m31 * pos.y + vp.m32 * pos.z + vp.m33;
            
            if (w <= 0.0001f) 
            {
                pxX = pxY = 0f;
                return;
            }

            // Manually inline matrix multiplication (Skipping the Z-Row entirely)
            float x = vp.m00 * pos.x + vp.m01 * pos.y + vp.m02 * pos.z + vp.m03;
            float y = vp.m10 * pos.x + vp.m11 * pos.y + vp.m12 * pos.z + vp.m13;

            // Mathematically simplified UV conversion: (clip / w + 1) * halfSize
            float invW = 1f / w;
            pxX = (x + w) * halfW * invW;
            pxY = (y + w) * halfH * invW;
        }
    }
}