using UnityEngine;

namespace OtterIK.Neo.Experiment
{
    /// <summary>
    /// 脊柱俯仰谐波（绕 X 轴），波从胸传到臀，臀部振幅最大。两种用法：
    /// TriggerHarmonic / TriggerSyncedHarmonic 打一次、自己衰减；
    /// Drive 每帧给相位和强度，持续摆动（例如冲刺时后腿并拢成"尾鳍"的上下波浪），StopDrive 停下。
    /// 叠加型：加在其它 provider 之上，不参与平均。
    /// </summary>
    public class ExpSpineHarmonicProvider : ExpSpinePoseProviderBase
    {
        [Header("Classic Cos Wave Settings")]
        public float maxAmplitude = 35f;
        public float frequency = 2.0f;
        public float decayRate = 8.0f;
        [Tooltip("波从头传到尾的延迟系数")]
        public float waveOffset = 0.8f;

        [Header("Chain")]
        [Tooltip("脊椎节数 (0 = Hip .. total-1 = Chest)；设了 decoupler 时用它的影子节点数")]
        public int totalJoints = 4;
        public SpineDecouplerWithShadows decoupler;

        private float _timer = 0f;
        private float _currentAmplitude = 0f;
        private float _currentFrequency = 2.0f;

        private bool _driven;
        private float _drivenPhase, _drivenAmplitude;

        public override bool Additive => true;

        public override bool Evaluate(int index, SpineChainDefinition spine, Transform jointOrShadow, Quaternion bindLocalRot, out Quaternion targetLocalRot, out float weight)
        {
            targetLocalRot = bindLocalRot;
            weight = 0f;

            float amplitude = _driven ? _drivenAmplitude * maxAmplitude : _currentAmplitude;
            if (amplitude < 0.01f) return false;

            // 1. 计算相位：使用 Cos 产生从最大值开始的“蹬水”起始感
            // (total - 1 - index) 确保力从 Chest 传向 Hip
            int total = decoupler != null && decoupler.shadowNodes.Count > 1 ? decoupler.shadowNodes.Count : Mathf.Max(2, totalJoints);
            float spatialOffset = (total - 1 - index) * waveOffset;
            float phase = (_driven ? _drivenPhase : _timer * _currentFrequency) * 2.0f * Mathf.PI - spatialOffset;

            // 2. 经典 Cos 波形
            float cosWave = Mathf.Cos(phase);

            // 3. 空间增益：胸腔稳定逻辑
            // index 0 (Hip) -> 1.5倍振幅
            // index total-1 (Chest) -> 0.4倍振幅 (尽可能稳定)
            float spatialGain = Mathf.Lerp(1.5f, 0.4f, (float)index / (total - 1));
            
            float angle = cosWave * amplitude * spatialGain;

            // 4. 垂直面运动 (绕 X 轴旋转)
            targetLocalRot = Quaternion.Euler(angle, 0, 0) * bindLocalRot;
            
            weight = globalWeight;
            return true;
        }

        private void Update()
        {
            if (_currentAmplitude > 0.01f)
            {
                _timer += Time.deltaTime;
                // 线性衰减，确保一次波动后迅速归零
                _currentAmplitude = Mathf.MoveTowards(_currentAmplitude, 0f, Time.deltaTime * decayRate);
            }
            else
            {
                _currentAmplitude = 0f;
                _timer = 0f;
            }
        }

        public void TriggerHarmonic(float intensity01) => TriggerSyncedHarmonic(0.3f, intensity01);

        public void TriggerSyncedHarmonic(float duration, float demand)
        {
            _timer = 0f;
            _currentFrequency = 1.0f / Mathf.Max(0.05f, duration);
            _currentAmplitude = maxAmplitude * demand;
        }

        /// <summary>持续摆动：phaseCycles 为周期相位（1 = 一整个周期），amplitude01 乘 maxAmplitude。每帧调用。</summary>
        public void Drive(float phaseCycles, float amplitude01)
        {
            _driven = true;
            _drivenPhase = phaseCycles;
            _drivenAmplitude = Mathf.Clamp01(amplitude01);
        }

        public void StopDrive() => _driven = false;
    }
}
