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
        
        // CrossFade переменные
        private bool _isCrossFading = false;
        private float _crossFadeStartTime = 0f;
        private float _crossFadeDuration = 0.25f;
        private GPUAnimation.Animation _crossFadeTargetAnimation;

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
                    instances[i, j].SetAnimation(skinned_mesh.anim.animations[4], speed: Random.Range(0.1f, 3.0f), start_time: Random.Range(0.0f, 1.0f));
                    instances[i, j].UpdateAll();
                }

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
            if (_isCrossFading)
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

        private void OnDestroy()
        {
            m.Dispose();
        }
    }
}