using System;
using UnityEngine;

namespace UnityVolumeRendering
{
    public class CentralDifferenceGradientComputator : GradientComputator
    {
        public CentralDifferenceGradientComputator(VolumeDataset dataset, bool smootheDataValues) : base(dataset, smootheDataValues)
        {
        }

        private float GetData(int x, int y, int z)
        {
            /*
            int dimZX = (int)Math.Sqrt(dimZ);
            //int dimZY = (dimZ + dimZX - 1) / dimZX;

            int bx = z % dimZX;
            int by = z / dimZX;

            //int i = x + y * dimX + z * (dimX * dimY);
            int u = x + bx * dimX;
            int v = y + by * dimY;
            int idx = u + v * dimX * dimZX;
            return data[idx];
            //*/

            return data[x + y * dimX + z * (dimX * dimY)]; // default
        }

        public override Vector3 ComputeGradient(int x, int y, int z, float minValue, float maxRange)
        {
            /*
            float x1 = data[Math.Min(x + 1, dimX - 1) + y * dimX + z * (dimX * dimY)] - minValue;
            float x2 = data[Math.Max(x - 1, 0) + y * dimX + z * (dimX * dimY)] - minValue;
            float y1 = data[x + Math.Min(y + 1, dimY - 1) * dimX + z * (dimX * dimY)] - minValue;
            float y2 = data[x + Math.Max(y - 1, 0) * dimX + z * (dimX * dimY)] - minValue;
            float z1 = data[x + y * dimX + Math.Min(z + 1, dimZ - 1) * (dimX * dimY)] - minValue;
            float z2 = data[x + y * dimX + Math.Max(z - 1, 0) * (dimX * dimY)] - minValue;
            //*/
            float x1 = GetData(Math.Min(x + 1, dimX - 1), y, z) - minValue;
            float x2 = GetData(Math.Max(x - 1, 0       ), y, z) - minValue;
            float y1 = GetData(x, Math.Min(y + 1, dimY - 1), z) - minValue;
            float y2 = GetData(x, Math.Max(y - 1, 0       ), z) - minValue;
            float z1 = GetData(x, y, Math.Min(z + 1, dimZ - 1)) - minValue;
            float z2 = GetData(x, y, Math.Max(z - 1, 0       )) - minValue;

            return new Vector3((x2 - x1) / maxRange, (y2 - y1) / maxRange, (z2 - z1) / maxRange);
        }
    }
}
