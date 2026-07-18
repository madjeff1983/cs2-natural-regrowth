using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

using Colossal.Collections;
using Colossal.Mathematics;

using Game;
using Game.Common;
using Game.Objects;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;

using TreeState = Game.Objects.TreeState;
using ReadOnlyAttribute = Unity.Collections.ReadOnlyAttribute;

namespace NaturalRegrowth
{
    /// <summary>
    /// Per-entity schedule for the next reproduction attempt, expressed as an
    /// absolute simulation frame. This replaces per-tick dice rolls: each tick
    /// we only compare this stored frame against "now", and only the handful of
    /// entities whose time has come do any real work.
    /// </summary>
    public struct ReproductionCooldown : IComponentData
    {
        public uint m_NextSpawnFrame;
    }

    public partial class NaturalRegrowthSystem : GameSystemBase
    {
        // How often (in frames) we scan for due reproducers. The scan itself is
        // a cheap Burst chunk pass; this just keeps it off the hot path.
        private const uint kScanIntervalFrames = 128;

        private SimulationSystem m_SimulationSystem;
        private TerrainSystem m_TerrainSystem;
        private Game.Objects.SearchSystem m_ObjectSearchSystem;
        private EndFrameBarrier m_Barrier;   // valid to create ECBs from in GameSimulation phase

        private EntityQuery m_VegetationQuery;
        private uint m_LastScanFrame;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TerrainSystem = World.GetOrCreateSystemManaged<TerrainSystem>();
            m_ObjectSearchSystem = World.GetOrCreateSystemManaged<Game.Objects.SearchSystem>();
            m_Barrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            // Eligible reproducers: trees (entities carrying a Tree component)
            // with a prefab/transform, excluding anything being deleted or that
            // is a temp/preview object.
            m_VegetationQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<Game.Objects.Tree>(),
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<Overridden>(),
                },
            });

            RequireForUpdate(m_VegetationQuery);
        }

        protected override void OnUpdate()
        {
            var setting = Mod.Setting;
            if (setting == null || setting.ReproductionRate <= 0)
                return; // reproduction paused

            uint frame = m_SimulationSystem.frameIndex;
            if (frame - m_LastScanFrame < kScanIntervalFrames)
                return;
            m_LastScanFrame = frame;

            // Map the 0..100 rate slider onto an interval range (frames between
            // spawns for a given adult). Higher rate => shorter interval.
            // Baseline intervals tuned to keep spread gradual even at speed.
            float rate01 = math.clamp(setting.ReproductionRate / 100f, 0.01f, 1f);
            uint minInterval = (uint)math.lerp(5325, 333, rate01);
            uint maxInterval = (uint)math.lerp(15974, 998, rate01);

            var heightData = m_TerrainSystem.GetHeightData(false);
            var searchTree = m_ObjectSearchSystem.GetStaticSearchTree(true, out var searchDep);

            var ecb = m_Barrier.CreateCommandBuffer();

            var job = new ReproductionJob
            {
                m_EntityType = GetEntityTypeHandle(),
                m_TreeType = GetComponentTypeHandle<Game.Objects.Tree>(true),
                m_TransformType = GetComponentTypeHandle<Transform>(true),
                m_PrefabRefType = GetComponentTypeHandle<PrefabRef>(true),
                m_CooldownType = GetComponentTypeHandle<ReproductionCooldown>(false),

                m_ObjectDataLookup = GetComponentLookup<ObjectData>(true),

                m_SearchTree = searchTree,
                m_HeightData = heightData,

                m_Frame = frame,
                m_MinInterval = minInterval,
                m_MaxInterval = maxInterval,
                m_DensityRadius = setting.SpawnRadius * 2f,
                m_SpawnRadius = setting.SpawnRadius,
                m_DensityCap = setting.LocalDensityCap,
                m_RandomSeed = RandomSeed.Next(),

                m_CommandBuffer = ecb,
            };

            var handle = job.Schedule(m_VegetationQuery,
                JobHandle.CombineDependencies(Dependency, searchDep));

            m_ObjectSearchSystem.AddStaticSearchTreeReader(handle);
            m_TerrainSystem.AddCPUHeightReader(handle);
            m_Barrier.AddJobHandleForProducer(handle);
            Dependency = handle;
        }

        [BurstCompile]
        private struct ReproductionJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public ComponentTypeHandle<Game.Objects.Tree> m_TreeType;
            [ReadOnly] public ComponentTypeHandle<Transform> m_TransformType;
            [ReadOnly] public ComponentTypeHandle<PrefabRef> m_PrefabRefType;
            public ComponentTypeHandle<ReproductionCooldown> m_CooldownType;

            [ReadOnly] public ComponentLookup<ObjectData> m_ObjectDataLookup;

            [ReadOnly] public NativeQuadTree<Entity, QuadTreeBoundsXZ> m_SearchTree;
            [ReadOnly] public TerrainHeightData m_HeightData;

            public uint m_Frame;
            public uint m_MinInterval;
            public uint m_MaxInterval;
            public float m_DensityRadius;
            public float m_SpawnRadius;
            public int m_DensityCap;
            public RandomSeed m_RandomSeed;

            public EntityCommandBuffer m_CommandBuffer;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex,
                bool useEnabledMask, in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(m_EntityType);
                var trees = chunk.GetNativeArray(ref m_TreeType);
                var transforms = chunk.GetNativeArray(ref m_TransformType);
                var prefabRefs = chunk.GetNativeArray(ref m_PrefabRefType);
                bool hasCooldown = chunk.Has(ref m_CooldownType);
                var cooldowns = hasCooldown ? chunk.GetNativeArray(ref m_CooldownType)
                                            : default;

                var rng = m_RandomSeed.GetRandom(unfilteredChunkIndex);

                for (int i = 0; i < chunk.Count; i++)
                {
                    var tree = trees[i];

                    // Adult gate. TreeState.Adult == 2. Only mature vegetation
                    // reproduces; saplings/teens/dead/stumps are skipped.
                    if ((tree.m_State & TreeState.Adult) == 0)
                        continue;

                    // Elderly/dying vegetation shouldn't reproduce.
                    if ((tree.m_State & (TreeState.Dead | TreeState.Stump | TreeState.Collected)) != 0)
                        continue;

                    Entity entity = entities[i];

                    // First time we see an eligible adult without a schedule:
                    // give it one and defer. (The structural add happens via ECB;
                    // next scan it will have the component.)
                    if (!hasCooldown)
                    {
                        m_CommandBuffer.AddComponent(entity,
                            new ReproductionCooldown
                            {
                                m_NextSpawnFrame = m_Frame + rng.NextUInt(m_MinInterval, m_MaxInterval),
                            });
                        continue;
                    }

                    var cd = cooldowns[i];

                    // Not scheduled yet (freshly added archetype default 0) —
                    // initialize and defer.
                    if (cd.m_NextSpawnFrame == 0u)
                    {
                        cd.m_NextSpawnFrame = m_Frame + rng.NextUInt(m_MinInterval, m_MaxInterval);
                        cooldowns[i] = cd;
                        continue;
                    }

                    // Not time yet — the cheap common case.
                    if (m_Frame < cd.m_NextSpawnFrame)
                        continue;

                    // --- Due to reproduce ---
                    float3 parentPos = transforms[i].m_Position;
                    Entity parentPrefab = prefabRefs[i].m_Prefab;

                    // Pick a candidate position in a ring around the parent.
                    // Minimum 3m so saplings don't spawn on top of the parent;
                    // maximum is the configured spawn radius.
                    float angle = rng.NextFloat(0f, math.PI * 2f);
                    float minDist = math.min(3f, m_SpawnRadius);
                    float dist = rng.NextFloat(minDist, m_SpawnRadius);
                    float3 candidate = parentPos + new float3(
                        math.cos(angle) * dist, 0f, math.sin(angle) * dist);
                    candidate.y = TerrainUtils.SampleHeight(ref m_HeightData, candidate);

                    // Local density check against the static object quadtree.
                    int count = CountNearby(candidate, m_DensityRadius);
                    if (count < m_DensityCap)
                    {
                        SpawnSapling(parentPrefab, candidate, ref rng);
                    }

                    // Reschedule regardless of success, so a blocked spot retries
                    // later rather than every scan.
                    cd.m_NextSpawnFrame = m_Frame + rng.NextUInt(m_MinInterval, m_MaxInterval);
                    cooldowns[i] = cd;
                }
            }

            private void SpawnSapling(Entity parentPrefab,
                float3 pos, ref Unity.Mathematics.Random rng)
            {
                if (!m_ObjectDataLookup.TryGetComponent(parentPrefab, out var objData))
                    return;

                // Recipe based on the working Line Tool mod (algernon-A):
                // create from the prefab's archetype, set PrefabRef + Transform,
                // set the Tree state, tag Updated, and disable Decoration (see
                // below). This produces a fully functional tree that grows and
                // shows resource info via the game's own systems.
                Entity child = m_CommandBuffer.CreateEntity(objData.m_Archetype);

                m_CommandBuffer.SetComponent(child,
                    new PrefabRef { m_Prefab = parentPrefab });

                quaternion rot = quaternion.RotateY(rng.NextFloat(0f, math.PI * 2f));
                m_CommandBuffer.SetComponent(child,
                    new Transform { m_Position = pos, m_Rotation = rot });

                // Spawn as the youngest sapling. state = 0 is valid (map-native
                // trees use it) and renders as a small sapling that then grows
                // through the game's TreeGrowthSystem toward Teen -> Adult.
                m_CommandBuffer.SetComponent(child, new Game.Objects.Tree
                {
                    m_State = 0,
                    m_Growth = 0,
                });

                // Line Tool finalizes a placed object with just Updated.
                m_CommandBuffer.AddComponent<Updated>(child);

                // The tree info panel's ResourceSection shows the WOOD row only
                // when Decoration is present but NOT enabled (verified from the
                // section's own visibility logic: it hides the row when
                // Decoration is enabled). Native map trees have Decoration
                // disabled, which is why they show WOOD. Archetype-created trees
                // may default to enabled, so we explicitly DISABLE it to match
                // native/placed trees and expose the WOOD attribute.
                m_CommandBuffer.SetComponentEnabled<Decoration>(child, false);
            }

            private int CountNearby(float3 pos, float radius)
            {
                var iterator = new CountIterator
                {
                    m_Bounds = new Bounds3(pos - radius, pos + radius),
                    m_Count = 0,
                };
                m_SearchTree.Iterate(ref iterator);
                return iterator.m_Count;
            }

            private struct CountIterator :
                INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>,
                IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
            {
                public Bounds3 m_Bounds;
                public int m_Count;

                public bool Intersect(QuadTreeBoundsXZ bounds)
                    => MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds.xz);

                public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
                {
                    if (MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds.xz))
                        m_Count++;
                }
            }
        }
    }
}