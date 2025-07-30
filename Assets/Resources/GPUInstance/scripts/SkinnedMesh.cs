using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using GPUAnimation;
using UnityEditor.VersionControl;
using Task = System.Threading.Tasks.Task;

namespace GPUInstance
{
    /// <summary>
    /// High-level abstraction that mimicks Skinned-Mesh behaviour using GPU instances.
    /// </summary>
    public struct SkinnedMesh
    {
        /// <summary>
        /// root gpu skinned mesh instance. All sub-mesh are parented to this one. To change the animation & transform of the entire skinned mesh.. you only need to update this single instance.
        /// </summary>
        public InstanceData<InstanceProperties> mesh;
        /// <summary>
        /// Skinned mesh can be composed of multiple gpu instances... These are all parented to 'mesh'. This way only one instance needs it transform updated.
        /// However... For some properties/other features you may need to update all GPU instances.
        /// </summary>
        public InstanceData<InstanceProperties>[] sub_mesh;
        public Skeleton skeleton;
        public bool _updateFlag;

        private ulong _anim_tick_start;
        private MeshInstancer m;
        private GPUAnimation.Animation _current_anim;
        private GPUAnimation.Animation _next_anim;
        private bool _init;

        // CrossFade state fields
        public bool isCrossFading;
        public float crossFadeStartTime;
        public float crossFadeDuration;
        public GPUAnimation.Animation crossFadeTargetAnimation;

        /// <summary>
        /// Number of GPU Instances that this object will use.
        /// </summary>
        public int InstanceCount { get { return skeleton.data.Length + 1; } }

        public SkinnedMesh(MeshType mesh, AnimationController anim, MeshInstancer m)
        {
            this.skeleton = new Skeleton(anim, m);
            this.mesh = new InstanceData<InstanceProperties>(mesh);
            this.m = m;
            this._init = false;
            this._anim_tick_start = 0;
            this._current_anim = null;
            this._next_anim = null;
            this.sub_mesh = null;
            
            // Initialize CrossFade fields
            this.isCrossFading = false;
            this.crossFadeStartTime = 0f;
            this.crossFadeDuration = 0f;
            this.crossFadeTargetAnimation = null;
            _updateFlag = false;
        }

        public SkinnedMesh(GPUSkinnedMeshComponent c, MeshInstancer m, int initial_lod=MeshInstancer.MaxLODLevel)
        {
            if (!c.Initialized())
                throw new System.Exception("Error, input GPUSkinnedMeshComponent must be added to MeshInstancer before creating instances!");

            this.skeleton = new Skeleton(c.anim, m);
            this.m = m;
            this._init = false;
            this._anim_tick_start = 0;
            this._current_anim = null;
            this._next_anim = null;
            
            // Initialize CrossFade fields
            this.isCrossFading = false;
            this.crossFadeStartTime = 0f;
            this.crossFadeDuration = 0f;
            this.crossFadeTargetAnimation = null;

            // create parent mesh instance
            this.mesh = new InstanceData<InstanceProperties>(c.MeshTypes[initial_lod][0]);

            // create other additional instances if this skinned mesh needs multiple instances
            if (c.MeshTypes[0].Length > 1)
            {
                this.sub_mesh = new InstanceData<InstanceProperties>[c.MeshTypes[0].Length - 1];
                for (int i = 1; i < c.MeshTypes[0].Length; i++)
                {
                    this.sub_mesh[i - 1] = new InstanceData<InstanceProperties>(c.MeshTypes[initial_lod][i]);
                }
            }
            else
            {
                this.sub_mesh = null;
            }
            
            _updateFlag = false;
        }

        /// <summary>
        /// Initialize skinned mesh. (Initializes child AnimationInstance skeleton as well)
        /// </summary>
        /// <param name="m"></param>
        public void Initialize(bool animation_culling = true)
        {
            if (mesh.groupID <= 0)
                throw new System.Exception("Error, no meshtype has been assigned to skinnedmesh");

            this.m.Initialize(ref mesh); // initialize root mesh

            this.mesh.props_AnimationCulling = animation_culling;
            this.mesh.props_animationID = skeleton.Controller.animations[0].GPUAnimationID; // just set to first animation
            this.mesh.props_AnimationSpeed = 1;
            this.mesh.props_instanceTicks = 0;
            this.mesh.skeletonID = m.GetNewSkeletonID(); // get skeleton id

            // Set the current animation reference
            this._current_anim = skeleton.Controller.animations[0];
            this._anim_tick_start = this.m.Ticks;

            // Initialize sub mesh
            if (!ReferenceEquals(null, this.sub_mesh))
            {
                for (int i = 0; i < this.sub_mesh.Length; i++)
                {
                    if (this.sub_mesh[i].groupID <= 0)
                        throw new System.Exception("Error, no meshtype has been assigned to skinnedmesh submesh");

                    this.m.Initialize(ref this.sub_mesh[i]);

                    this.sub_mesh[i].props_AnimationCulling = animation_culling;
                    this.sub_mesh[i].props_animationID = skeleton.Controller.animations[0].GPUAnimationID; // just set to first animation
                    this.sub_mesh[i].props_AnimationSpeed = 1;
                    this.sub_mesh[i].props_instanceTicks = 0;
                    this.sub_mesh[i].skeletonID = this.mesh.skeletonID; // get skeleton id

                    this.sub_mesh[i].parentID = this.mesh.id; // parent to root
                }
            }

            this.skeleton.InitializeInstance(m, skeletonID: this.mesh.skeletonID, bone_type: mesh.groupID, radius: 2.0f * this.mesh.radius, property_id: mesh.propertyID); // initialize skeleton
            this.skeleton.SetRootParent(mesh.id); // parent the skeleton to the mesh

            this._init = true;
        }

        public bool Initialized()
        {
            return this._init;
        }

        /// <summary>
        /// Get Position,Rotation,Scale of this skinned mesh- even if it moving along a path.
        /// </summary>
        /// <param name="path"></param>
        /// <param name="p"></param>
        /// <param name="position"></param>
        /// <param name="rotation"></param>
        /// <param name="scale"></param>
        public void CalcTRS(in Path path, in PathArrayHelper p, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = this.mesh.position;
            rotation = this.mesh.rotation;
            scale = this.mesh.scale;

            if (path.path_id > 0)
            {
                Vector3 direction, up;
                p.CalculatePathPositionDirection(path, this.mesh, out position, out direction, out up);
                rotation = Quaternion.LookRotation(direction, up);
            }
        }

        public Matrix4x4 CalcBone2World(in Path path, in PathArrayHelper p, int bone)
        {
            Vector3 position; Quaternion rotation; Vector3 scale;
            CalcTRS(path, p, out position, out rotation, out scale);
            Matrix4x4 mesh2world = Matrix4x4.TRS(position, rotation, scale);
            
            // Use CrossFade-aware bone calculation if CrossFade is active
            if (this.isCrossFading && this.mesh.props_animationBlend > 0.0f)
            {
                return CalcBone2WorldWithCrossFade(mesh2world, bone);
            }
            
            // Ensure we have a valid animation
            if (this._current_anim == null)
            {
                // Try to get the first available animation
                if (this.skeleton.Controller.animations.Length > 0)
                {
                    this._current_anim = this.skeleton.Controller.animations[0];
                }
                else
                {
                    // Return identity matrix if no animation is available
                    return mesh2world;
                }
            }
            
            return this.skeleton.CalculateBone2World(mesh2world, bone, this._current_anim, this._anim_tick_start, this.mesh);
        }

        public Matrix4x4 CalcBone2World(int bone)
        {
            Matrix4x4 mesh2world = Matrix4x4.TRS(mesh.position, mesh.rotation, mesh.scale);
            
            // Use CrossFade-aware bone calculation if CrossFade is active
            if (this.isCrossFading && this.mesh.props_animationBlend > 0.0f)
            {
                return CalcBone2WorldWithCrossFade(mesh2world, bone);
            }
            
            // Ensure we have a valid animation
            if (this._current_anim == null)
            {
                // Try to get the first available animation
                if (this.skeleton.Controller.animations.Length > 0)
                {
                    this._current_anim = this.skeleton.Controller.animations[0];
                }
                else
                {
                    // Return identity matrix if no animation is available
                    return mesh2world;
                }
            }
            
            return this.skeleton.CalculateBone2World(mesh2world, bone, this._current_anim, this._anim_tick_start, this.mesh);
        }

        /// <summary>
        /// Calculate bone world matrix with CrossFade blending between two animations
        /// This mirrors the GPU shader logic for proper CPU-GPU synchronization
        /// </summary>
        private Matrix4x4 CalcBone2WorldWithCrossFade(Matrix4x4 mesh2world, int bone)
        {
            if (bone < 0 || bone >= this.skeleton.Controller.BoneCount)
                throw new System.Exception("Error, input an invalid bone index");

            // Get the current animation (Animation A) and target animation (Animation B)
            var animA = this._current_anim;
            var animB = this.crossFadeTargetAnimation;
            
            // Ensure we have valid animations
            if (animA == null)
            {
                if (this.skeleton.Controller.animations.Length > 0)
                {
                    animA = this.skeleton.Controller.animations[0];
                    this._current_anim = animA;
                }
                else
                {
                    return mesh2world; // Return identity if no animation available
                }
            }
            
            if (animB == null)
            {
                // If target animation is null, just use animation A
                return CalculateBoneMatrixForAnimation(mesh2world, bone, animA, this.mesh.props_instanceTicks);
            }

            // Calculate bone matrices for both animations using the same time logic as GPU shader
            // GPU uses props.instanceTicks for animation A and instanceTicks_B for animation B
            Matrix4x4 boneMatrixA = CalculateBoneMatrixForAnimation(mesh2world, bone, animA, this.mesh.props_instanceTicks);
            Matrix4x4 boneMatrixB = CalculateBoneMatrixForAnimation(mesh2world, bone, animB, this.mesh.props_instanceTicks_B);

            // Blend between the two bone matrices using the current blend factor
            float blend = this.mesh.props_animationBlend;
            return BlendBoneMatrices(boneMatrixA, boneMatrixB, blend);
        }

        /// <summary>
        /// Calculate bone matrix for a specific animation at a specific time
        /// </summary>
        private Matrix4x4 CalculateBoneMatrixForAnimation(Matrix4x4 mesh2world, int bone, GPUAnimation.Animation animation, uint instanceTicks)
        {
            if (bone == AnimationController.kRootBoneID)
                return mesh2world;

            // Calculate parent bone matrix recursively
            Matrix4x4 parentMatrix = CalculateBoneMatrixForAnimation(mesh2world, this.skeleton.Controller.bone_parents[bone], animation, instanceTicks);

            // Get bone animation data
            var boneAnim = animation.boneAnimations[bone];
            
            // Calculate animation time for this bone
            float t = CalculateAnimationTimeForBone(bone, boneAnim, instanceTicks);
            
            // Interpolate bone transform
            Vector3 position = boneAnim.InterpPosition(t);
            Quaternion rotation = boneAnim.InterpRotation(t);
            Vector3 scale = boneAnim.InterpScale(t);
            
            Matrix4x4 boneTransform = Matrix4x4.TRS(position, rotation, scale);
            return parentMatrix * boneTransform;
        }

        /// <summary>
        /// Calculate animation time for a specific bone, mirroring the GPU shader logic
        /// </summary>
        private float CalculateAnimationTimeForBone(int bone, BoneAnimation boneAnim, uint instanceTicks)
        {
            uint clipTickLen = boneAnim.AnimationTickLength;
            uint animSpeed = this.mesh.props_AnimationSpeedRaw;
            bool loop = !this.mesh.props_AnimationPlayOnce;

            // Calculate elapsed time using the same logic as GPU shader
            ulong elapsedTicks = ((this.m.Ticks - this._anim_tick_start) * animSpeed) / 10;
            elapsedTicks += instanceTicks;

            // Calculate current tick
            ulong animTick;
            if (loop)
            {
                animTick = elapsedTicks % clipTickLen;
            }
            else
            {
                animTick = elapsedTicks >= clipTickLen ? clipTickLen - 1 : elapsedTicks % clipTickLen;
            }

            return (float)animTick / (float)clipTickLen;
        }

        /// <summary>
        /// Blend between two bone matrices using position, rotation, and scale interpolation
        /// </summary>
        private Matrix4x4 BlendBoneMatrices(Matrix4x4 matrixA, Matrix4x4 matrixB, float blend)
        {
            // Decompose both matrices
            Vector3 positionA, positionB;
            Quaternion rotationA, rotationB;
            Vector3 scaleA, scaleB;
            
            DecomposeMatrix(matrixA, out positionA, out rotationA, out scaleA);
            DecomposeMatrix(matrixB, out positionB, out rotationB, out scaleB);

            // Interpolate position, rotation, and scale
            Vector3 blendedPosition = Vector3.Lerp(positionA, positionB, blend);
            Quaternion blendedRotation = Quaternion.Slerp(rotationA, rotationB, blend);
            Vector3 blendedScale = Vector3.Lerp(scaleA, scaleB, blend);

            // Reconstruct the blended matrix
            return Matrix4x4.TRS(blendedPosition, blendedRotation, blendedScale);
        }

        /// <summary>
        /// Decompose a matrix into position, rotation, and scale
        /// </summary>
        private void DecomposeMatrix(Matrix4x4 matrix, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = matrix.GetColumn(3);
            
            Vector3 forward = matrix.GetColumn(2);
            Vector3 up = matrix.GetColumn(1);
            rotation = Quaternion.LookRotation(forward, up);
            
            scale = new Vector3(
                matrix.GetColumn(0).magnitude,
                matrix.GetColumn(1).magnitude,
                matrix.GetColumn(2).magnitude
            );
        }

        public void BoneWorldTRS(in Path path, in PathArrayHelper p, int bone, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            var b2w = CalcBone2World(path, p, bone);
            decompose(b2w, out position, out rotation, out scale);
        }

        public void BoneWorldTRS(int bone, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            var b2w = CalcBone2World(bone);
            decompose(b2w, out position, out rotation, out scale);
        }

        void decompose(in Matrix4x4 b2w, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            position = b2w.GetColumn(3);
            rotation = Quaternion.LookRotation(b2w.GetColumn(2), b2w.GetColumn(1));
            scale = new Vector3(b2w.GetColumn(0).magnitude, b2w.GetColumn(1).magnitude, b2w.GetColumn(2).magnitude);
        }

        /// <summary>
        /// Set radius on skinned mesh.
        /// </summary>
        /// <param name="radius"></param>
        public void SetRadius(in float radius)
        {
            this.mesh.radius = radius;
            this.mesh.DirtyFlags = this.mesh.DirtyFlags | DirtyFlag.radius;

            if (!ReferenceEquals(null, this.sub_mesh))
            {
                for (int i = 0; i < this.sub_mesh.Length; i++)
                {
                    this.sub_mesh[i].radius = radius;
                    this.sub_mesh[i].DirtyFlags = this.sub_mesh[i].DirtyFlags | DirtyFlag.radius;
                }
            }
        }

        /// <summary>
        /// Set the animation
        /// </summary>
        /// <param name="animation"></param>
        /// <param name="bone"></param>
        /// <param name="speed"></param>
        /// <param name="start_time"></param>
        public void SetAnimation(string animation, float speed = 1, float start_time = 0, bool loop = true)
        {
            // This version is safer than SetAnimation with raw animation- animations guarentted to work on this skinned mesh
            var a = skeleton.Controller.namedAnimations[animation];
            SetAnimation(a, speed, start_time, loop);
        }

        /// <summary>
        /// Set the animation
        /// </summary>
        /// <param name="animation"></param>
        /// <param name="bone"></param>
        /// <param name="speed"></param>
        /// <param name="start_time"></param>
        public void SetAnimation(GPUAnimation.Animation animation, float speed = 1, float start_time = 0, bool loop = true)
        {
            //TODO? Add a safe version that check if the animation can even be played by this skinned mesh- (ie, if it doesn't belong then you get spaghetti model)

            if (!_init)
                throw new System.Exception("Error, skinned mesh is not initialized.");

            this.mesh.props_animationID = animation.GPUAnimationID;// animation.boneAnimations[n].id;
            this.mesh.props_instanceTicks = (uint)(start_time * Ticks.TicksPerSecond); // reset ticks
            this.mesh.props_AnimationSpeed = speed;
            this.mesh.props_AnimationPlayOnce = !loop;
            this.mesh.DirtyFlags = this.mesh.DirtyFlags | DirtyFlag.props_AnimationID | DirtyFlag.props_Extra | DirtyFlag.props_InstanceTicks;
            this._current_anim = animation;
            this._anim_tick_start = this.m.Ticks;
        }

        public void SetAnimationBlend(GPUAnimation.Animation animA, GPUAnimation.Animation animB, float blend, float speedA = 1, float speedB = 1, float startA = 0, float startB = 0, bool loopA = true, bool loopB = true)
        {
            if (!_init)
                throw new System.Exception("Error, skinned mesh is not initialized.");

            // Сохраняем текущее время анимации, если анимации не меняются
            uint currentTicks = this.mesh.props_instanceTicks;
            uint currentTicks_B = this.mesh.props_instanceTicks_B;
            
            // Сбрасываем время только если анимации действительно изменились
            bool animAChanged = (this.mesh.props_animationID != animA.GPUAnimationID);
            bool animBChanged = (this.mesh.props_animationID_B != animB.GPUAnimationID);
            
            this.mesh.props_animationID = animA.GPUAnimationID;
            this.mesh.props_animationID_B = animB.GPUAnimationID;
            this.mesh.props_instanceTicks = animAChanged ? (uint)(startA * Ticks.TicksPerSecond) : currentTicks;
            this.mesh.props_instanceTicks_B = animBChanged ? (uint)(startB * Ticks.TicksPerSecond) : currentTicks_B;
            this.mesh.props_animationBlend = Mathf.Clamp01(blend);
            this.mesh.props_AnimationSpeed = speedA; // NB: only speedA for now
            this.mesh.props_AnimationPlayOnce = !loopA;
            this._current_anim = animA;
            this._next_anim = animB;
            
            // Устанавливаем флаги для обновления всех blending полей
            this.mesh.DirtyFlags = this.mesh.DirtyFlags | DirtyFlag.props_AnimationID | DirtyFlag.props_InstanceTicks | DirtyFlag.props_AnimationBlend;

            if (!ReferenceEquals(null, this.sub_mesh))
            {
                for (int i = 0; i < this.sub_mesh.Length; i++)
                {
                    uint subCurrentTicks = this.sub_mesh[i].props_instanceTicks;
                    uint subCurrentTicks_B = this.sub_mesh[i].props_instanceTicks_B;
                    
                    this.sub_mesh[i].props_animationID = animA.GPUAnimationID;
                    this.sub_mesh[i].props_animationID_B = animB.GPUAnimationID;
                    this.sub_mesh[i].props_instanceTicks = animAChanged ? (uint)(startA * Ticks.TicksPerSecond) : subCurrentTicks;
                    this.sub_mesh[i].props_instanceTicks_B = animBChanged ? (uint)(startB * Ticks.TicksPerSecond) : subCurrentTicks_B;
                    this.sub_mesh[i].props_animationBlend = Mathf.Clamp01(blend);
                    this.sub_mesh[i].props_AnimationSpeed = speedA;
                    this.sub_mesh[i].props_AnimationPlayOnce = !loopA;
                    this.sub_mesh[i].DirtyFlags = this.sub_mesh[i].DirtyFlags | DirtyFlag.props_AnimationID | DirtyFlag.props_InstanceTicks | DirtyFlag.props_AnimationBlend;
                }
            }
        }

        /// <summary>
        /// Изменяет только blend factor без сброса анимаций
        /// </summary>
        public void SetBlendFactor(float blend)
        {
            if (!_init)
                throw new System.Exception("Error, skinned mesh is not initialized.");

            this.mesh.props_animationBlend = Mathf.Clamp01(blend);
            this.mesh.DirtyFlags = this.mesh.DirtyFlags | DirtyFlag.props_AnimationBlend;
            
            if (!ReferenceEquals(null, this.sub_mesh))
            {
                for (int i = 0; i < this.sub_mesh.Length; i++)
                {
                    this.sub_mesh[i].props_animationBlend = Mathf.Clamp01(blend);
                    this.sub_mesh[i].DirtyFlags = this.sub_mesh[i].DirtyFlags | DirtyFlag.props_AnimationBlend;
                }
            }
        }

        /// <summary>
        /// Complete the current CrossFade by making animation B the main animation
        /// </summary>
        public void CompleteCrossFade()
        {
            if (!_init || !this.isCrossFading)
                return;

            // Make animation B the main animation
            this.mesh.props_animationID = this.mesh.props_animationID_B;
            this.mesh.props_instanceTicks = this.mesh.props_instanceTicks_B;
            
            // Reset blend
            this.mesh.props_animationBlend = 0.0f;
            
            // Clear animation B
            this.mesh.props_animationID_B = 0;
            this.mesh.props_instanceTicks_B = 0;
            
            // Set flags for update
            this.mesh.DirtyFlags = this.mesh.DirtyFlags | DirtyFlag.props_AnimationID | DirtyFlag.props_InstanceTicks | 
                                   DirtyFlag.props_AnimationID_B | DirtyFlag.props_InstanceTicks_B | DirtyFlag.props_AnimationBlend;

            // Update current animation reference
            this._current_anim = this.crossFadeTargetAnimation;
            
            if (!ReferenceEquals(null, this.sub_mesh))
            {
                for (int i = 0; i < this.sub_mesh.Length; i++)
                {
                    this.sub_mesh[i].props_animationID = this.sub_mesh[i].props_animationID_B;
                    this.sub_mesh[i].props_instanceTicks = this.sub_mesh[i].props_instanceTicks_B;
                    this.sub_mesh[i].props_animationBlend = 0.0f;
                    this.sub_mesh[i].props_animationID_B = 0;
                    this.sub_mesh[i].props_instanceTicks_B = 0;
                    
                    this.sub_mesh[i].DirtyFlags = this.sub_mesh[i].DirtyFlags | DirtyFlag.props_AnimationID | DirtyFlag.props_InstanceTicks | 
                                                  DirtyFlag.props_AnimationID_B | DirtyFlag.props_InstanceTicks_B | DirtyFlag.props_AnimationBlend;
                }
            }

            // Clear CrossFade state
            this.isCrossFading = false;
            this.crossFadeStartTime = 0f;
            this.crossFadeDuration = 0f;
            this.crossFadeTargetAnimation = null;
        }


        public void OnRefreshCrossFade()
        {
            if (!isCrossFading)
                return;

            float elapsedTime = Time.time - crossFadeStartTime;
            float progress = Mathf.Clamp01(elapsedTime / crossFadeDuration);

            // Простая линейная интерполяция
            float currentBlend = progress;
            SetBlendFactor(currentBlend);

            // Завершаем CrossFade когда достигли 100%
            if (progress >= 1.0f)
            {
                CompleteCrossFade();
            }

            UpdateAllInOtherThread();
        }

        /// <summary>
        /// Плавно переходит к новой анимации через CrossFade
        /// </summary>
        /// <param name="newAnimation">Новая анимация</param>
        /// <param name="fadeTime">Время перехода в секундах</param>
        /// <param name="speed">Скорость новой анимации</param>
        /// <param name="startTime">Время начала новой анимации</param>
        /// <param name="loop">Зацикливать ли новую анимацию</param>
        public void CrossFade(GPUAnimation.Animation newAnimation, float fadeTime = 0.25f, float speed = 1, float startTime = 0, bool loop = true) =>
           this.CrossFade(newAnimation, fadeTime, speed, startTime, loop, Time.time);
        
        /// <summary>
        /// Плавно переходит к новой анимации через CrossFade
        /// </summary>
        /// <param name="newAnimation">Новая анимация</param>
        /// <param name="fadeTime">Время перехода в секундах</param>
        /// <param name="speed">Скорость новой анимации</param>
        /// <param name="startTime">Время начала новой анимации</param>
        /// <param name="loop">Зацикливать ли новую анимацию</param>
        public void CrossFade(GPUAnimation.Animation newAnimation, float fadeTime = 0.25f, float speed = 1, float startTime = 0, bool loop = true, float time = 0)
        {
            if (!_init)
                throw new System.Exception("Error, skinned mesh is not initialized.");

            // Store the target animation for CPU-side calculations
            this.crossFadeTargetAnimation = newAnimation;
            
            // Get current time of animation A for synchronization
            uint currentTicksA = this.mesh.props_instanceTicks;
            
            // Initialize animation B with the same time as animation A to prevent jumps
            // This ensures both animations start from the same point in time
            this.mesh.props_animationID_B = newAnimation.GPUAnimationID;
            this.mesh.props_instanceTicks_B = currentTicksA; // Use same time as A for smooth transition
            this.mesh.props_AnimationSpeed = speed;
            this.mesh.props_AnimationPlayOnce = !loop;
            
            // Start with blend = 0 (100% animation A)
            this.mesh.props_animationBlend = 0.0f;
            
            // Set flags for updating B fields and blend
            this.mesh.DirtyFlags = this.mesh.DirtyFlags | DirtyFlag.props_AnimationID_B | DirtyFlag.props_InstanceTicks_B | DirtyFlag.props_AnimationBlend | DirtyFlag.props_Extra;
           
            // Store CrossFade state
            this.isCrossFading = true;
            this.crossFadeStartTime = time;
            this.crossFadeDuration = fadeTime;
            
            // Update sub-meshes if they exist
            if (!ReferenceEquals(null, this.sub_mesh))
            {
                for (int i = 0; i < this.sub_mesh.Length; i++)
                {
                    this.sub_mesh[i].props_animationID_B = newAnimation.GPUAnimationID;
                    this.sub_mesh[i].props_instanceTicks_B = currentTicksA; // Use same time as A
                    this.sub_mesh[i].props_AnimationSpeed = speed;
                    this.sub_mesh[i].props_AnimationPlayOnce = !loop;
                    this.sub_mesh[i].props_animationBlend = 0.0f;
                    
                    this.sub_mesh[i].DirtyFlags = this.sub_mesh[i].DirtyFlags | DirtyFlag.props_AnimationID_B | DirtyFlag.props_InstanceTicks_B | DirtyFlag.props_AnimationBlend | DirtyFlag.props_Extra;
                }
            }
        }

        /// <summary>
        /// Update the mesh instance data & update the animation instance skeleton & update gpu skeleton map.  Note* do not call Update() & dispose() in the same frame. It will cause a race condition on the gpu.
        /// </summary>
        public void UpdateAll()
        {
            if (!_init)
                throw new System.Exception("Error, skinned mesh is not initialized. Cannot update.");
            this.m.Append(ref mesh);

            if (!ReferenceEquals(null, this.sub_mesh))
            {
                this.m.AppendMany(this.sub_mesh);
            }

            skeleton.Update();
        }

        public void UpdateAllInOtherThread()
        {
            _updateFlag = true;
        }
        
        
        /// <summary>
        /// Updates only the root instance.
        /// </summary>
        public void UpdateRoot()
        {
            if (!_init)
                throw new System.Exception("Error, skinned mesh is not initialized. Cannot update.");
            this.m.Append(ref mesh);
        }

        /// <summary>
        /// Update root mesh & any sub mesh
        /// </summary>
        public void UpdateMesh()
        {
            if (!_init)
                throw new System.Exception("Error, skinned mesh is not initialized. Cannot update.");
            this.m.Append(ref mesh);

            if (!ReferenceEquals(null, this.sub_mesh))
            {
                this.m.AppendMany(this.sub_mesh);
            }
        }

        /// <summary>
        /// Free all GPU resources held by this object. 
        /// </summary>
        public void Dispose()
        {
            if (!_init)
                throw new System.Exception("Error, skinned mesh has not been initialized. Cannot dispose.");
            this.skeleton.Dispose();
            this.skeleton = default(Skeleton);
            this.m.Delete(ref this.mesh);

            if (!ReferenceEquals(null, this.sub_mesh))
            {
                this.m.DeleteMany(this.sub_mesh);
            }
            this.sub_mesh = null;

            this.m.ReleaseSkeletonID(this.mesh.skeletonID);
            this.m = null;
            this._current_anim = null;
            this._anim_tick_start = 0;
            this.mesh = default(InstanceData<InstanceProperties>);
            this._init = false;
        }
    }
}
