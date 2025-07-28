using System.Collections;
using System.Collections.Generic;
using GPUInstance;
using UnityEngine;

using InstanceData = GPUInstance.InstanceData<GPUInstance.InstanceProperties>;
namespace GPUInstanceTest
{
    public class parasiteanimdemo : MonoBehaviour
    {
        [Range(0, 1)]
        public float blend;
        public GPUInstance.GPUSkinnedMeshComponent skinned_mesh;
        public int NInstancesSqrd = 10;
        public Camera FrustumCullingCamera = null;

        private GPUInstance.MeshInstancer m;
        private GPUInstance.SkinnedMesh[,] instances;

        private GameObject[] bones_unity = null;

        private GPUAnimation.Animation _animA, _animB;
        private float _blend = 0f;
        private float _lastBlend = -1f; // Для отслеживания изменений
        private ulong _animA_tick_start, _animB_tick_start;
        
        // CrossFade переменные для инстанса [0,0] (для ручного управления)
        private bool _isCrossFading = false;
        private float _crossFadeStartTime = 0f;
        private float _crossFadeDuration = 0.25f;
        private GPUAnimation.Animation _crossFadeTargetAnimation;
        
        // CrossFade переменные для автоматического режима
        private bool[,] _autoCrossFading;
        private float[,] _autoCrossFadeStartTime;
        private float[,] _autoCrossFadeDuration;
        private GPUAnimation.Animation[,] _autoCrossFadeTargetAnimation;
        
        // Автоматический CrossFade
        [Header("Auto CrossFade Settings")]
        public bool enableAutoCrossFade = true;
        public bool useCrossFade = true; // Галочка для сравнения
        public float autoCrossFadeInterval = 2.0f;
        [Range(0.1f, 3.0f)]
        public float crossFadeDuration = 0.5f; // Длительность CrossFade (короткая для быстрого перехода)
        private float _lastAutoCrossFadeTime = 0f;
        private int _currentAutoAnimationIndex = 0;

        public void SetAnimationBlend(GPUAnimation.Animation a, GPUAnimation.Animation b, float blend, float speedA = 1, float speedB = 1, float startA = 0, float startB = 0)
        {
            _animA = a;
            _animB = b;
            _blend = Mathf.Clamp01(blend);
            _animA_tick_start = this.m.Ticks;
            _animB_tick_start = this.m.Ticks;
            // Можно добавить поддержку скоростей и времени старта для каждой анимации
        }
        
        /// <summary>
        /// Запускает CrossFade к новой анимации
        /// </summary>
        public void StartCrossFade(GPUAnimation.Animation newAnimation, float fadeTime = 0.25f)
        {
            if (_isCrossFading)
            {
                // Если уже идет CrossFade, завершаем его мгновенно
                CompleteCrossFade();
            }
            
            _isCrossFading = true;
            _crossFadeStartTime = Time.time;
            _crossFadeDuration = fadeTime;
            _crossFadeTargetAnimation = newAnimation;
            
            // Запускаем CrossFade
            instances[0, 0].CrossFade(newAnimation, fadeTime);
            instances[0, 0].UpdateRoot();
        }
        
        /// <summary>
        /// Завершает текущий CrossFade
        /// </summary>
        private void CompleteCrossFade()
        {
            if (_isCrossFading)
            {
                _isCrossFading = false;
                
                // Плавно завершаем CrossFade, устанавливая blend = 1
                instances[0, 0].SetBlendFactor(1.0f);
                instances[0, 0].UpdateRoot();
                
                // Теперь анимация B становится основной анимацией
                // Это происходит автоматически в следующем кадре через compute shader
            }
        }
        
        /// <summary>
        /// Завершает автоматический CrossFade для конкретного инстанса
        /// </summary>
        private void CompleteAutoCrossFade(int i, int j)
        {
            // Делаем анимацию B основной (анимация A)
            instances[i, j].mesh.props_animationID = instances[i, j].mesh.props_animationID_B;
            instances[i, j].mesh.props_instanceTicks = instances[i, j].mesh.props_instanceTicks_B;
            
            // Сбрасываем blend
            instances[i, j].mesh.props_animationBlend = 0.0f;
            
            // Очищаем анимацию B
            instances[i, j].mesh.props_animationID_B = 0;
            instances[i, j].mesh.props_instanceTicks_B = 0;
            
            // Устанавливаем флаги для обновления
            instances[i, j].mesh.DirtyFlags = instances[i, j].mesh.DirtyFlags | 
                                             DirtyFlag.props_AnimationID | DirtyFlag.props_InstanceTicks | 
                                             DirtyFlag.props_AnimationID_B | DirtyFlag.props_InstanceTicks_B | 
                                             DirtyFlag.props_AnimationBlend | DirtyFlag.props_Extra;
            
            instances[i, j].UpdateRoot();
            
            // Отладочная информация
            if (i == 0 && j == 0)
            {
                Debug.Log($"CompleteAutoCrossFade: Animation A now {instances[i, j].mesh.props_animationID}, Blend reset to {instances[i, j].mesh.props_animationBlend:F3}");
            }
        }
        
        /// <summary>
        /// Автоматический CrossFade для половины инстансов
        /// </summary>
        private void AutoCrossFade()
        {
            if (!enableAutoCrossFade) return;
            
            if (Time.time - _lastAutoCrossFadeTime >= autoCrossFadeInterval)
            {
                _lastAutoCrossFadeTime = Time.time;
                
                // Выбираем следующую анимацию
                _currentAutoAnimationIndex = (_currentAutoAnimationIndex + 1) % skinned_mesh.anim.animations.Length;
                GPUAnimation.Animation nextAnimation = skinned_mesh.anim.animations[_currentAutoAnimationIndex];
                
                // Применяем к половине инстансов
                int halfCount = NInstancesSqrd / 2;
                
                for (int i = 0; i < NInstancesSqrd; i++)
                {
                    for (int j = 0; j < NInstancesSqrd; j++)
                    {
                            if (useCrossFade)
                            {
                                // Запускаем CrossFade для этого инстанса
                                _autoCrossFading[i, j] = true;
                                _autoCrossFadeStartTime[i, j] = Time.time;
                                _autoCrossFadeDuration[i, j] = crossFadeDuration;
                                _autoCrossFadeTargetAnimation[i, j] = nextAnimation;
                                
                                // Вызываем CrossFade
                                instances[i, j].CrossFade(nextAnimation, crossFadeDuration);
                                instances[i, j].UpdateRoot();
                            }
                            else
                            {
                                // Обычная смена анимации без CrossFade
                                instances[i, j].SetAnimation(nextAnimation);
                                instances[i, j].UpdateRoot();
                            }
                    }
                }
                
                Debug.Log($"Auto animation change: {(_currentAutoAnimationIndex + 1)}/{skinned_mesh.anim.animations.Length} " +
                         $"({(useCrossFade ? "with CrossFade" : "without CrossFade")}) for {halfCount} instances");
            }
        }

        // Start is called before the first frame update
        void Start()
        {
            // Initialize animation controller
            skinned_mesh.anim.Initialize();
            int NumBonesInSkeleton = skinned_mesh.anim.BoneCount;
            int MaxHierarchyDepth = skinned_mesh.anim.BoneHierarchyDepth + 2; // hierarchy for each bone depth + one for mesh and an additional for attaching to bones

            // Create and initialize mesh instancer
            m = new GPUInstance.MeshInstancer();
            m.Initialize(num_skeleton_bones: NumBonesInSkeleton, max_parent_depth: MaxHierarchyDepth);

            // Set animations buffer
            m.SetAllAnimations(new GPUAnimation.AnimationController[1] { skinned_mesh.anim });

            // Create a mesh type for the parasite model
            m.AddGPUSkinnedMeshType(this.skinned_mesh, override_shadows: true, shadow_mode: UnityEngine.Rendering.ShadowCastingMode.Off, receive_shadows: false); // spawn using lowest detail model (you can use any but this will be faster- this way the initial frame doesnt render with all high-detail models)

            // Create instances
            instances = new GPUInstance.SkinnedMesh[NInstancesSqrd, NInstancesSqrd];
            for (int i = 0; i < NInstancesSqrd; i++)
                for (int j = 0; j < NInstancesSqrd; j++)
                {
                    instances[i, j] = new GPUInstance.SkinnedMesh(this.skinned_mesh, this.m); // make new skinned mesh
                    instances[i, j].mesh.position = new Vector3(i * 1.5f, 0, j * 1.5f); // set position
                    instances[i, j].SetRadius(1.75f); // assign radius large enough so that the model doesn't get culled too early
                    instances[i, j].Initialize();
                    //instances[i, j].SetAnimation(skinned_mesh.anim.animations[4], speed: Random.Range(0.1f, 3.0f), start_time: Random.Range(0.0f, 1.0f));
                    
                    // Инициализируем анимацию B для CrossFade
                    instances[i, j].mesh.props_animationID_B = skinned_mesh.anim.animations[4].GPUAnimationID;
                    instances[i, j].mesh.props_instanceTicks_B = (uint)(Random.Range(0.0f, 1.0f) * GPUInstance.Ticks.TicksPerSecond);
                    instances[i, j].mesh.props_animationBlend = 0.0f; // Начинаем с 100% анимации A
                    
                    instances[i, j].UpdateAll();
                }
                
            // Инициализируем массивы для автоматического CrossFade
            _autoCrossFading = new bool[NInstancesSqrd, NInstancesSqrd];
            _autoCrossFadeStartTime = new float[NInstancesSqrd, NInstancesSqrd];
            _autoCrossFadeDuration = new float[NInstancesSqrd, NInstancesSqrd];
            _autoCrossFadeTargetAnimation = new GPUAnimation.Animation[NInstancesSqrd, NInstancesSqrd];

            //// visualize bones on the 0,0 model
            //var points = new InstanceData[instances[0, 0].skeleton.data.Length];
            //for (int i = 0; i < points.Length; i++)
            //{
            //    points[i] = new InstanceData(m.mesh.Default);
            //    points[i].parentID = instances[0, 0].skeleton.data[i].id;
            //    points[i].scale = new Vector3(0.03f, 0.03f, 0.03f);
            //    points[i].props_color32 = Color.red;
            //    m.Initialize(ref points[i]);
            //    m.Append(ref points[i]);
            //}

            bones_unity = new GameObject[instances[0, 0].skeleton.data.Length];
            for (int i = 0; i < bones_unity.Length; i++)
            {
                var obj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obj.name = "Calculated Bone Transform " + i.ToString();
                bones_unity[i] = obj;
            }
            
            // Инициализируем blending
            _lastBlend = blend;
            instances[0, 0].SetAnimationBlend(skinned_mesh.anim.animations[2], skinned_mesh.anim.animations[4], blend);
            instances[0, 0].UpdateRoot();
        }

        int f = 1;
        // Update is called once per frame
        void Update()
        {
            // Assign frustum culling camera
            m.FrustumCamera = FrustumCullingCamera;
            
                        // Применяем blending только при изменении значения
            if (Mathf.Abs(blend - _lastBlend) > 0.001f)
            {
                // Изменяем только blend factor без сброса анимаций
                instances[0, 0].SetBlendFactor(blend);
                instances[0, 0].UpdateRoot(); // Обновляем изменения на GPU
                _lastBlend = blend;
            }
            
            // Вторая анимация обновляется автоматически в compute shader
            
            // Обновляем CrossFade
            /*if (_isCrossFading)
            {
                float elapsedTime = Time.time - _crossFadeStartTime;
                float progress = Mathf.Clamp01(elapsedTime / _crossFadeDuration);
                
                // Простая линейная интерполяция
                float currentBlend = progress;
                instances[0, 0].SetBlendFactor(currentBlend);
                instances[0, 0].UpdateRoot();
                
                // Завершаем CrossFade когда достигли 100%
                if (progress >= 1.0f)
                {
                    CompleteCrossFade();
                }
            }*/
            
            // Автоматический CrossFade
            AutoCrossFade();
            
            // Обновляем автоматический CrossFade для всех инстансов
            if (enableAutoCrossFade && useCrossFade)
            {
                for (int i = 0; i < NInstancesSqrd; i++)
                {
                    for (int j = 0; j < NInstancesSqrd; j++)
                    {
                        if (_autoCrossFading[i, j])
                        {
                            float elapsedTime = Time.time - _autoCrossFadeStartTime[i, j];
                            float progress = Mathf.Clamp01(elapsedTime / _autoCrossFadeDuration[i, j]);
                            
                            // Простая линейная интерполяция
                            float currentBlend = progress;
                            instances[i, j].SetBlendFactor(currentBlend);
                            instances[i, j].UpdateRoot();
                            
                            // Завершаем CrossFade когда достигли 100%
                            if (progress >= 1.0f)
                            {
                                _autoCrossFading[i, j] = false;
                                
                                // Завершаем CrossFade правильно
                                CompleteAutoCrossFade(i, j);
                                
                                // Отладочная информация при завершении
                                if (i == 0 && j == 0)
                                {
                                    Debug.Log($"Auto CrossFade completed for instance [0,0] - Final blend: {currentBlend:F3}");
                                }
                            }
                        }
                    }
                }
            }
            
            // Run update
            m.Update(Time.deltaTime);


            if (Input.GetKeyDown(KeyCode.Alpha1))
            {
                CrossFadeToAnim0();
            } 
            else if (Input.GetKeyDown(KeyCode.Alpha2))
            {
                CrossFadeToAnim1();
            }
            else if (Input.GetKeyDown(KeyCode.Alpha3))
            {
                CrossFadeToAnim2();
            }
            else if (Input.GetKeyDown(KeyCode.Alpha4))
            {
                CrossFadeToAnim3();
            }
            else if (Input.GetKeyDown(KeyCode.Alpha5))
            {
                CrossFadeToAnim4();
            }

            // visualize bone cpu-gpu synchronization
            for (int i = 0; i < bones_unity.Length; i++)
            {
                Vector3 position; Quaternion rotation; Vector3 scale;
                instances[0, 0].BoneWorldTRS(i, out position, out rotation, out scale);
                bones_unity[i].transform.position = position;
                bones_unity[i].transform.rotation = rotation;
                bones_unity[i].transform.localScale = new Vector3(0.05f, 0.05f, 0.05f);
            }

            f++;
        }


        [ContextMenu("Refresh")]
        private void Refresh()
        {
            instances[0, 0].SetAnimationBlend(skinned_mesh.anim.animations[4], skinned_mesh.anim.animations[2], blend);
            instances[0, 0].UpdateRoot();
        }
        
        [ContextMenu("CrossFade to Animation 0")]
        private void CrossFadeToAnim0()
        {
            StartCrossFade(skinned_mesh.anim.animations[0], 0.3f);
        }
        
        [ContextMenu("CrossFade to Animation 1")]
        private void CrossFadeToAnim1()
        {
            StartCrossFade(skinned_mesh.anim.animations[1], 0.3f);
        }
        
        [ContextMenu("CrossFade to Animation 2")]
        private void CrossFadeToAnim2()
        {
            StartCrossFade(skinned_mesh.anim.animations[2], 0.3f);
        }
        
        [ContextMenu("CrossFade to Animation 3")]
        private void CrossFadeToAnim3()
        {
            StartCrossFade(skinned_mesh.anim.animations[3], 0.3f);
        }
        
        [ContextMenu("CrossFade to Animation 4")]
        private void CrossFadeToAnim4()
        {
            StartCrossFade(skinned_mesh.anim.animations[4], 0.3f);
        }
        
        [ContextMenu("Toggle Auto CrossFade")]
        private void ToggleAutoCrossFade()
        {
            enableAutoCrossFade = !enableAutoCrossFade;
            Debug.Log($"Auto CrossFade: {(enableAutoCrossFade ? "ON" : "OFF")}");
        }
        
        [ContextMenu("Toggle CrossFade Mode")]
        private void ToggleCrossFadeMode()
        {
            useCrossFade = !useCrossFade;
            Debug.Log($"CrossFade Mode: {(useCrossFade ? "ON" : "OFF")}");
        }
        
        [ContextMenu("Reset All Animations")]
        private void ResetAllAnimations()
        {
            for (int i = 0; i < NInstancesSqrd; i++)
            {
                for (int j = 0; j < NInstancesSqrd; j++)
                {
                    instances[i, j].SetAnimation(skinned_mesh.anim.animations[0]);
                    instances[i, j].UpdateRoot();
                }
            }
            _currentAutoAnimationIndex = 0;
            Debug.Log("All animations reset to animation 0");
        }

        private void OnDestroy()
        {
            m.Dispose();
        }
    }
}