using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Playable greybox for the 5-minute survivor prototype.
/// Pixel art is loaded from Resources/SurvivorArt; procedural circles remain as safe visual fallbacks.
/// </summary>
public class SurvivorPrototype : MonoBehaviour
{
    enum RunState { Title, Playing, LevelUp, Paused, Result }
    enum EnemyKind { Grunt, Runner, Tank, Elite, Boss }
    enum WeaponKind { Fireball, Slash, Aura }
    enum PassiveKind { Power, Boots, Vitality }

    sealed class Enemy
    {
        public GameObject go;
        public float hp, maxHp, speed, touchDamage, xp, radius;
        public EnemyKind kind;
        public float attackTimer, animationTimer;
        public string facing = "down";
        public Sprite[] frames;
        public bool alive = true;
    }
    sealed class Shot
    {
        public GameObject go;
        public Vector2 velocity;
        public float damage, life;
    }
    sealed class Gem
    {
        public GameObject go;
        public float value;
    }
    sealed class Card
    {
        public string title, description, key;
        public Action apply;
    }

    const float RunDuration = 300f;
    const float PlayerRadius = .38f;
    const float InvulnerableDuration = .75f;
    const int MaxEnemies = 90;

    static Sprite circleSprite;
    static Sprite squareSprite;
    static Texture2D pixelTexture;

    readonly List<Enemy> enemies = new List<Enemy>();
    readonly List<Shot> shots = new List<Shot>();
    readonly List<Gem> gems = new List<Gem>();
    readonly List<GameObject> potions = new List<GameObject>();
    readonly Dictionary<WeaponKind, int> weaponLevels = new Dictionary<WeaponKind, int>();
    readonly Dictionary<PassiveKind, int> passiveLevels = new Dictionary<PassiveKind, int>();
    readonly Dictionary<string, Sprite[]> playerWalkFrames = new Dictionary<string, Sprite[]>();
    readonly Dictionary<string, Sprite> playerIdleSprites = new Dictionary<string, Sprite>();
    readonly Dictionary<string, Sprite[]> enemyFrameCache = new Dictionary<string, Sprite[]>();
    Sprite[] fireballFrames;
    Sprite experienceSprite, potionSprite, fieldTileSprite;
    SpriteRenderer playerRenderer;
    GameObject background;
    string playerFacing = "down";
    float playerAnimationTimer;

    RunState state = RunState.Title;
    GameObject player;
    Camera gameCamera;
    float hp, maxHp, xp, xpToNext, elapsed, invulnerable, spawnTimer;
    float fireTimer, slashTimer, auraTimer, hitFlash;
    float moveSpeed, damageBonus, cooldownBonus, pickupRadius, armor;
    int level, kills;
    bool bossSpawned, eliteSpawned;
    Enemy boss;
    string resultHeading = "";
    List<Card> activeCards;

    void Awake()
    {
        EnsureSprites();
        LoadArt();
        gameCamera = Camera.main;
        if (gameCamera == null)
        {
            var cameraObject = new GameObject("Main Camera");
            gameCamera = cameraObject.AddComponent<Camera>();
            cameraObject.tag = "MainCamera";
        }
        gameCamera.orthographic = true;
        gameCamera.orthographicSize = 6f;
        gameCamera.transform.position = new Vector3(0, 0, -10);
        gameCamera.backgroundColor = new Color(.035f, .05f, .085f);
        gameCamera.clearFlags = CameraClearFlags.SolidColor;
        CreateBackground();
        CreatePlayer();
        ResetRun();
    }

    void LoadArt()
    {
        string[] directions = { "down", "up", "left", "right" };
        foreach (string direction in directions)
        {
            playerWalkFrames[direction] = LoadFrames("SurvivorArt/Player/Walk/" + direction);
            Sprite[] idle = LoadFrames("SurvivorArt/Player/Idle/" + direction);
            if (idle.Length > 0) playerIdleSprites[direction] = idle[0];
        }
        fireballFrames = LoadFrames("SurvivorArt/Effects/Fireball");
        experienceSprite = Resources.Load<Sprite>("SurvivorArt/Items/Experience/gem");
        potionSprite = Resources.Load<Sprite>("SurvivorArt/Items/Recovery/potion");
        fieldTileSprite = Resources.Load<Sprite>("SurvivorArt/Environment/Field/field_ground_tiled");
    }

    Sprite[] LoadFrames(string resourcePath)
    {
        Sprite[] frames = Resources.LoadAll<Sprite>(resourcePath);
        Array.Sort(frames, (a, b) => string.CompareOrdinal(a.name, b.name));
        return frames;
    }

    Sprite[] GetEnemyFrames(EnemyKind kind, string direction)
    {
        string key = kind + "/" + direction;
        if (enemyFrameCache.TryGetValue(key, out Sprite[] cached)) return cached;
        string resourcePath = kind == EnemyKind.Boss
            ? "SurvivorArt/Boss/Tengu/Idle"
            : "SurvivorArt/Enemies/" + kind + "/" + direction;
        Sprite[] frames = LoadFrames(resourcePath);
        enemyFrameCache[key] = frames;
        return frames;
    }

    static void SetAnimatedFrame(GameObject target, Sprite sprite)
    {
        if (target == null || sprite == null) return;
        var renderer = target.GetComponent<SpriteRenderer>();
        if (renderer != null) renderer.sprite = sprite;
    }

    static void FitSprite(GameObject target, Sprite sprite, float desiredSize)
    {
        if (target == null || sprite == null) return;
        var renderer = target.GetComponent<SpriteRenderer>();
        if (renderer == null) return;
        renderer.sprite = sprite;
        renderer.color = Color.white;
        float nativeSize = Mathf.Max(sprite.bounds.size.x, sprite.bounds.size.y);
        if (nativeSize > 0.001f)
            target.transform.localScale = Vector3.one * (desiredSize / nativeSize);
    }

    static void EnsureSprites()
    {
        if (circleSprite != null) return;
        pixelTexture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        pixelTexture.filterMode = FilterMode.Point;
        pixelTexture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[64 * 64];
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            float dx = (x + .5f - 32f) / 31f;
            float dy = (y + .5f - 32f) / 31f;
            pixels[y * 64 + x] = dx * dx + dy * dy <= 1f ? Color.white : Color.clear;
        }
        pixelTexture.SetPixels(pixels);
        pixelTexture.Apply();
        circleSprite = Sprite.Create(pixelTexture, new Rect(0, 0, 64, 64), new Vector2(.5f, .5f), 64);
        var square = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        square.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
        square.filterMode = FilterMode.Point;
        square.Apply();
        squareSprite = Sprite.Create(square, new Rect(0, 0, 2, 2), new Vector2(.5f, .5f), 2);
    }

    void CreateBackground()
    {
        background = new GameObject("Field Ground");
        background.transform.position = new Vector3(0, 0, 5);
        var sr = background.AddComponent<SpriteRenderer>();
        // Keep the play area visually quiet so the pixel-art characters and effects read clearly.
        // The source pack's isolated field tiles are corner/edge pieces, so tiling one creates a noisy peach pattern.
        sr.sprite = squareSprite;
        sr.sortingOrder = -100;
        background.transform.localScale = new Vector3(300, 300, 1);
        sr.color = new Color(.045f, .075f, .09f);
    }

    void CreatePlayer()
    {
        player = MakeSprite("Survivor", Vector2.zero, .76f, Color.white, 10);
        playerRenderer = player.GetComponent<SpriteRenderer>();
        if (playerIdleSprites.TryGetValue("down", out Sprite idle))
            FitSprite(player, idle, .76f);
    }

    GameObject MakeSprite(string objectName, Vector2 position, float size, Color color, int order)
    {
        var go = new GameObject(objectName);
        go.transform.position = position;
        go.transform.localScale = Vector3.one * size;
        var renderer = go.AddComponent<SpriteRenderer>();
        renderer.sprite = circleSprite;
        renderer.color = color;
        renderer.sortingOrder = order;
        return go;
    }

    void ResetRun()
    {
        ClearWorldObjects();
        state = RunState.Title;
        hp = maxHp = 100;
        xp = 0;
        xpToNext = 8;
        elapsed = 0;
        level = 1;
        kills = 0;
        invulnerable = 0;
        spawnTimer = 0;
        fireTimer = .6f;
        slashTimer = 1f;
        auraTimer = .5f;
        bossSpawned = false;
        eliteSpawned = false;
        boss = null;
        moveSpeed = 4.5f;
        damageBonus = cooldownBonus = 0;
        pickupRadius = 1.6f;
        armor = 0;
        weaponLevels.Clear();
        passiveLevels.Clear();
        weaponLevels[WeaponKind.Fireball] = 1;
        player.transform.position = Vector3.zero;
        player.SetActive(true);
        CreatePotions();
    }

    void ClearWorldObjects()
    {
        foreach (var e in enemies) if (e.go != null) Destroy(e.go);
        foreach (var s in shots) if (s.go != null) Destroy(s.go);
        foreach (var g in gems) if (g.go != null) Destroy(g.go);
        foreach (var p in potions) if (p != null) Destroy(p);
        enemies.Clear(); shots.Clear(); gems.Clear(); potions.Clear();
    }

    void Update()
    {
        if (state == RunState.Title)
        {
            if (WasPressed(Key.Enter) || WasPressed(Key.Space)) state = RunState.Playing;
            else return;
        }
        if (state == RunState.Result) return;
        if (WasPressed(Key.Escape))
        {
            if (state == RunState.Playing) state = RunState.Paused;
            else if (state == RunState.Paused) state = RunState.Playing;
        }
        if (state == RunState.LevelUp)
        {
            if (activeCards != null)
            {
                for (int i = 0; i < activeCards.Count && i < 3; i++)
                    if (WasPressed((Key)((int)Key.Digit1 + i))) { SelectCard(activeCards[i]); break; }
            }
            return;
        }
        if (state != RunState.Playing) return;

        float dt = Time.deltaTime;
        elapsed += dt;
        invulnerable = Mathf.Max(0, invulnerable - dt);
        hitFlash = Mathf.Max(0, hitFlash - dt);

        MovePlayer(dt);
        UpdateSpawning(dt);
        UpdateEnemies(dt);
        UpdateShots(dt);
        UpdateGems(dt);
        UpdatePotions();
        UpdateWeapons(dt);
        CheckRunEnd();
    }

    bool WasPressed(Key key) => Keyboard.current != null && Keyboard.current[key].wasPressedThisFrame;
    bool IsHeld(Key key) => Keyboard.current != null && Keyboard.current[key].isPressed;

    void MovePlayer(float dt)
    {
        float x = 0, y = 0;
        if (IsHeld(Key.A) || IsHeld(Key.LeftArrow)) x--;
        if (IsHeld(Key.D) || IsHeld(Key.RightArrow)) x++;
        if (IsHeld(Key.S) || IsHeld(Key.DownArrow)) y--;
        if (IsHeld(Key.W) || IsHeld(Key.UpArrow)) y++;
        Vector2 direction = new Vector2(x, y).normalized;
        player.transform.position += (Vector3)(direction * moveSpeed * dt);
        if (direction.sqrMagnitude > 0.001f)
        {
            playerFacing = DirectionName(direction);
            playerAnimationTimer += dt;
            if (playerWalkFrames.TryGetValue(playerFacing, out Sprite[] walk) && walk.Length > 0)
            {
                int frame = Mathf.FloorToInt(playerAnimationTimer * 9f) % walk.Length;
                SetAnimatedFrame(player, walk[frame]);
            }
        }
        else if (playerIdleSprites.TryGetValue(playerFacing, out Sprite idle))
            FitSprite(player, idle, .76f);

        gameCamera.transform.position = new Vector3(player.transform.position.x, player.transform.position.y, -10);
        if (background != null)
            background.transform.position = new Vector3(player.transform.position.x, player.transform.position.y, 5);
    }

    string DirectionName(Vector2 direction)
    {
        if (Mathf.Abs(direction.x) > Mathf.Abs(direction.y))
            return direction.x < 0 ? "left" : "right";
        return direction.y < 0 ? "down" : "up";
    }

    void UpdateSpawning(float dt)
    {
        if (elapsed >= 180f && !eliteSpawned)
        {
            eliteSpawned = true;
            SpawnEnemy(EnemyKind.Elite, false);
        }
        if (elapsed >= RunDuration && !bossSpawned)
        {
            bossSpawned = true;
            spawnTimer = 999;
            SpawnEnemy(EnemyKind.Boss, true);
        }
        if (bossSpawned || enemies.Count >= MaxEnemies) return;
        float rate = elapsed < 60 ? 1.25f : elapsed < 150 ? .8f : elapsed < 240 ? .55f : .36f;
        spawnTimer -= dt;
        if (spawnTimer <= 0)
        {
            spawnTimer = rate;
            float roll = UnityEngine.Random.value;
            EnemyKind kind = elapsed < 60 ? EnemyKind.Grunt :
                roll < .58f ? EnemyKind.Grunt : roll < .82f ? EnemyKind.Runner : EnemyKind.Tank;
            SpawnEnemy(kind, false);
        }
    }

    Vector2 SpawnPosition()
    {
        float halfH = gameCamera.orthographicSize + 1.25f;
        float halfW = halfH * gameCamera.aspect + 1.25f;
        int side = UnityEngine.Random.Range(0, 4);
        Vector2 p;
        if (side < 2) p = new Vector2(UnityEngine.Random.Range(-halfW, halfW), side == 0 ? -halfH : halfH);
        else p = new Vector2(side == 2 ? -halfW : halfW, UnityEngine.Random.Range(-halfH, halfH));
        return (Vector2)player.transform.position + p;
    }

    void SpawnEnemy(EnemyKind kind, bool isBoss)
    {
        Vector2 p = SpawnPosition();
        float waveHp = elapsed < 120 ? 1 : elapsed < 240 ? 1.5f : 2.2f;
        float size, baseHp, speed, touch, reward;
        Color color;
        switch (kind)
        {
            case EnemyKind.Runner:
                size = .55f; baseHp = 12; speed = 3.2f; touch = 6; reward = 1; color = new Color(1f, .48f, .28f); break;
            case EnemyKind.Tank:
                size = 1.05f; baseHp = 70; speed = 1.25f; touch = 15; reward = 3; color = new Color(.62f, .48f, .9f); break;
            case EnemyKind.Elite:
                size = 1.08f; baseHp = 144; speed = 1.52f; touch = 16; reward = 25; color = new Color(1f, .78f, .22f); break;
            case EnemyKind.Boss:
                size = 2.3f; baseHp = 900; speed = 1.55f; touch = 22; reward = 0; color = new Color(.95f, .18f, .27f); break;
            default:
                size = .72f; baseHp = 18; speed = 1.9f; touch = 8; reward = 1; color = new Color(.86f, .42f, .32f); break;
        }
        if (isBoss) { kind = EnemyKind.Boss; color = new Color(.95f, .18f, .27f); }
        float scaledHp = kind == EnemyKind.Boss ? baseHp : baseHp * waveHp;
        var e = new Enemy
        {
            kind = kind, hp = scaledHp, maxHp = scaledHp, speed = speed, touchDamage = touch,
            xp = reward, radius = size * .5f,
            go = MakeSprite(isBoss ? "Rift Guardian" : kind.ToString(), p, size, Color.white, kind == EnemyKind.Boss ? 5 : 3)
        };
        e.frames = GetEnemyFrames(kind, "down");
        if (e.frames.Length > 0) FitSprite(e.go, e.frames[0], size);
        if (isBoss) boss = e;
        enemies.Add(e);
    }

    void UpdateEnemies(float dt)
    {
        Vector2 playerPos = player.transform.position;
        for (int i = enemies.Count - 1; i >= 0; i--)
        {
            Enemy e = enemies[i];
            if (e.go == null) { enemies.RemoveAt(i); continue; }
            Vector2 pos = e.go.transform.position;
            Vector2 toPlayer = playerPos - pos;
            float distance = toPlayer.magnitude;
            if (e.kind == EnemyKind.Boss)
            {
                e.animationTimer += dt;
                if (e.frames != null && e.frames.Length > 0)
                    SetAnimatedFrame(e.go, e.frames[Mathf.FloorToInt(e.animationTimer * 7f) % e.frames.Length]);
                e.attackTimer -= dt;
                if (e.attackTimer <= 0 && distance > 1.2f)
                {
                    e.attackTimer = 3.4f;
                    StartCoroutine(BossCharge(e, toPlayer.normalized));
                }
                else if (e.attackTimer > 2.6f)
                    e.go.transform.position += (Vector3)(toPlayer.normalized * e.speed * dt);
            }
            else
            {
                e.go.transform.position += (Vector3)(toPlayer.normalized * e.speed * dt);
                e.facing = DirectionName(toPlayer);
                e.frames = GetEnemyFrames(e.kind, e.facing);
                e.animationTimer += dt;
                if (e.frames.Length > 0)
                    SetAnimatedFrame(e.go, e.frames[Mathf.FloorToInt(e.animationTimer * 8f) % e.frames.Length]);
            }

            float contact = e.radius + PlayerRadius;
            if (distance <= contact && invulnerable <= 0)
                TakeDamage(Mathf.Max(1, e.touchDamage - armor));
        }
    }

    System.Collections.IEnumerator BossCharge(Enemy e, Vector2 dir)
    {
        if (e.go == null) yield break;
        // Telegraph by briefly turning gold, then make one readable dash.
        var sr = e.go.GetComponent<SpriteRenderer>();
        if (sr != null) sr.color = new Color(1f, .75f, .2f);
        yield return new WaitForSeconds(.8f);
        if (e.go == null || state != RunState.Playing) yield break;
        if (sr != null) sr.color = new Color(.95f, .18f, .27f);
        float timer = 0;
        while (timer < .38f && e.go != null && state == RunState.Playing)
        {
            e.go.transform.position += (Vector3)(dir * 11f * Time.deltaTime);
            timer += Time.deltaTime;
            if (Vector2.Distance(e.go.transform.position, player.transform.position) < e.radius + PlayerRadius && invulnerable <= 0)
                TakeDamage(Mathf.Max(1, e.touchDamage - armor));
            yield return null;
        }
    }

    void UpdateShots(float dt)
    {
        for (int i = shots.Count - 1; i >= 0; i--)
        {
            Shot s = shots[i];
            s.life -= dt;
            s.go.transform.position += (Vector3)(s.velocity * dt);
            bool hit = false;
            for (int j = enemies.Count - 1; j >= 0; j--)
            {
                Enemy e = enemies[j];
                if (e.go == null) continue;
                if (Vector2.Distance(s.go.transform.position, e.go.transform.position) <= e.radius + .16f)
                {
                    DamageEnemy(e, s.damage);
                    hit = true;
                    break;
                }
            }
            if (hit || s.life <= 0)
            {
                Destroy(s.go);
                shots.RemoveAt(i);
            }
        }
    }

    void UpdateGems(float dt)
    {
        Vector2 p = player.transform.position;
        for (int i = gems.Count - 1; i >= 0; i--)
        {
            Gem g = gems[i];
            Vector2 gp = g.go.transform.position;
            float distance = Vector2.Distance(p, gp);
            if (distance < pickupRadius)
                g.go.transform.position = Vector2.MoveTowards(gp, p, 8f * dt);
            if (distance < PlayerRadius + .18f)
            {
                xp += g.value;
                Destroy(g.go);
                gems.RemoveAt(i);
                TryLevelUp();
            }
        }
    }

    void CreatePotions()
    {
        Vector2[] positions = { new Vector2(8, 0), new Vector2(-8, 0), new Vector2(0, 8), new Vector2(0, -8) };
        foreach (Vector2 position in positions)
        {
            var potion = MakeSprite("Recovery Potion", position, .48f, new Color(.32f, 1f, .46f), 2);
            if (potionSprite != null) FitSprite(potion, potionSprite, .48f);
            potions.Add(potion);
        }
    }

    void UpdatePotions()
    {
        for (int i = potions.Count - 1; i >= 0; i--)
        {
            GameObject potion = potions[i];
            if (potion == null) { potions.RemoveAt(i); continue; }
            if (Vector2.Distance(player.transform.position, potion.transform.position) <= pickupRadius + PlayerRadius)
            {
                hp = Mathf.Min(maxHp, hp + 30);
                Destroy(potion);
                potions.RemoveAt(i);
            }
        }
    }

    void UpdateWeapons(float dt)
    {
        Enemy target = NearestEnemy();
        if (weaponLevels.ContainsKey(WeaponKind.Fireball))
        {
            fireTimer -= dt;
            float cooldown = 1.2f * (1f - cooldownBonus);
            if (fireTimer <= 0 && target != null)
            {
                int lv = weaponLevels[WeaponKind.Fireball];
                int count = lv >= 5 ? 3 : lv >= 3 ? 2 : 1;
                float damage = (lv >= 5 ? 22 : lv >= 4 ? 18 : lv >= 2 ? 14 : 10) * (1 + damageBonus);
                Vector2 dir = ((Vector2)target.go.transform.position - (Vector2)player.transform.position).normalized;
                for (int n = 0; n < count; n++)
                {
                    float angle = (n - (count - 1) / 2f) * 12f;
                    Vector2 shotDir = Quaternion.Euler(0, 0, angle) * dir;
                    var shotGo = MakeSprite("Fireball", player.transform.position + (Vector3)(shotDir * .5f), .28f, new Color(1f, .5f, .17f), 7);
                    if (fireballFrames.Length > 0) FitSprite(shotGo, fireballFrames[0], .38f);
                    shots.Add(new Shot { go = shotGo, velocity = shotDir * 8f, damage = damage, life = 2f });
                }
                fireTimer = cooldown;
            }
        }

        if (weaponLevels.TryGetValue(WeaponKind.Slash, out int slashLv))
        {
            slashTimer -= dt;
            if (slashTimer <= 0 && target != null)
            {
                float range = slashLv >= 5 ? 5f : slashLv >= 3 ? 4f : 3.2f;
                float damage = (slashLv >= 5 ? 32 : slashLv >= 4 ? 26 : slashLv >= 3 ? 22 : slashLv == 2 ? 18 : 14) * (1 + damageBonus);
                Vector2 dir = ((Vector2)target.go.transform.position - (Vector2)player.transform.position).normalized;
                ShowPulse("Slash Arc", (Vector2)player.transform.position + dir * (range * .42f), range * .8f, new Color(1f, .83f, .32f, .68f), .16f);
                for (int i = enemies.Count - 1; i >= 0 && state == RunState.Playing; i--)
                {
                    Enemy e = enemies[i];
                    Vector2 to = (Vector2)e.go.transform.position - (Vector2)player.transform.position;
                    if (to.magnitude <= range && Vector2.Angle(dir, to) <= 60f) DamageEnemy(e, damage);
                }
                slashTimer = (slashLv >= 4 ? 1f : 1.6f) * (1 - cooldownBonus);
            }
        }

        if (weaponLevels.TryGetValue(WeaponKind.Aura, out int auraLv))
        {
            auraTimer -= dt;
            if (auraTimer <= 0)
            {
                float range = auraLv >= 5 ? 2.5f : auraLv >= 4 ? 2.1f : auraLv >= 3 ? 1.85f : auraLv == 2 ? 1.6f : 1.35f;
                float damage = (auraLv >= 5 ? 8 : auraLv >= 4 ? 6 : auraLv >= 3 ? 5 : auraLv == 2 ? 4 : 3) * (1 + damageBonus);
                ShowPulse("Magic Field", player.transform.position, range * 2f, new Color(.35f, .68f, 1f, .35f), .2f);
                for (int i = enemies.Count - 1; i >= 0 && state == RunState.Playing; i--)
                {
                    Enemy e = enemies[i];
                    if (Vector2.Distance(e.go.transform.position, player.transform.position) <= range + e.radius)
                        DamageEnemy(e, damage);
                }
                auraTimer = (auraLv >= 4 ? .7f : .8f) * (1 - cooldownBonus);
            }
        }
    }

    void ShowPulse(string name, Vector2 pos, float diameter, Color color, float lifetime)
    {
        var go = MakeSprite(name, pos, diameter, color, 4);
        Destroy(go, lifetime);
    }

    Enemy NearestEnemy()
    {
        Enemy best = null;
        float bestD = float.MaxValue;
        foreach (Enemy e in enemies)
        {
            if (e.go == null) continue;
            float d = (e.go.transform.position - player.transform.position).sqrMagnitude;
            if (d < bestD) { bestD = d; best = e; }
        }
        return best;
    }

    void DamageEnemy(Enemy e, float damage)
    {
        if (e == null || e.go == null) return;
        e.hp -= damage;
        if (e.hp <= 0)
        {
            if (e.kind == EnemyKind.Boss) { Destroy(e.go); enemies.Remove(e); boss = null; resultHeading = "승리! 균열 수호자를 처치했습니다"; state = RunState.Result; return; }
            Vector2 p = e.go.transform.position;
            var gem = MakeSprite("Experience", p, .3f, new Color(.3f, .95f, 1f), 2);
            gems.Add(new Gem { go = gem, value = e.xp });
            if (e.kind == EnemyKind.Elite) hp = Mathf.Min(maxHp, hp + 20);
            Destroy(e.go);
            enemies.Remove(e);
            kills++;
        }
    }

    void TakeDamage(float amount)
    {
        if (invulnerable > 0 || state != RunState.Playing) return;
        hp = Mathf.Max(0, hp - amount);
        invulnerable = InvulnerableDuration;
        hitFlash = .22f;
        if (hp <= 0) { resultHeading = "패배"; state = RunState.Result; }
    }

    void TryLevelUp()
    {
        if (state != RunState.Playing || xp < xpToNext) return;
        xp -= xpToNext;
        level++;
        xpToNext += 4;
        state = RunState.LevelUp;
        activeCards = BuildCards();
    }

    List<Card> BuildCards()
    {
        var candidates = new List<Card>();
        AddWeaponCard(candidates, WeaponKind.Fireball, "화염구", "가장 가까운 적에게 불덩이를 발사합니다.");
        AddWeaponCard(candidates, WeaponKind.Slash, "참격", "가까운 적 방향의 부채꼴을 베어냅니다.");
        AddWeaponCard(candidates, WeaponKind.Aura, "마력장", "주변 적에게 주기적으로 피해를 줍니다.");
        AddPassiveCard(candidates, PassiveKind.Power, "힘의 인장", "모든 무기 피해가 10% 증가합니다.");
        AddPassiveCard(candidates, PassiveKind.Boots, "질주의 장화", "이동 속도가 8% 증가합니다.");
        AddPassiveCard(candidates, PassiveKind.Vitality, "생명의 부적", "최대 체력 20 증가, 체력 20 회복.");
        if (hp < maxHp - 1 || candidates.Count == 0) candidates.Add(new Card { title = "회복", description = "체력을 30 회복합니다.", key = "회복", apply = () => hp = Mathf.Min(maxHp, hp + 30) });

        for (int i = candidates.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
        }
        if (candidates.Count > 3) candidates.RemoveRange(3, candidates.Count - 3);
        return candidates;
    }

    void AddWeaponCard(List<Card> list, WeaponKind kind, string name, string desc)
    {
        int owned = weaponLevels.TryGetValue(kind, out int lv) ? lv : 0;
        if ((owned == 0 && weaponLevels.Count >= 3) || owned >= 5) return;
        bool isNew = owned == 0;
        list.Add(new Card
        {
            title = name + (isNew ? " · 신규" : " · Lv." + owned + " → " + (owned + 1)),
            description = desc,
            key = name,
            apply = () => weaponLevels[kind] = (weaponLevels.TryGetValue(kind, out int current) ? current : 0) + 1
        });
    }

    void AddPassiveCard(List<Card> list, PassiveKind kind, string name, string desc)
    {
        int owned = passiveLevels.TryGetValue(kind, out int lv) ? lv : 0;
        if ((owned == 0 && passiveLevels.Count >= 3) || owned >= 5) return;
        list.Add(new Card
        {
            title = name + (owned == 0 ? " · 신규" : " · Lv." + owned + " → " + (owned + 1)),
            description = desc,
            key = name,
            apply = () =>
            {
                passiveLevels[kind] = (passiveLevels.TryGetValue(kind, out int current) ? current : 0) + 1;
                if (kind == PassiveKind.Power) damageBonus += .1f;
                if (kind == PassiveKind.Boots) moveSpeed *= 1.08f;
                if (kind == PassiveKind.Vitality) { maxHp += 20; hp = Mathf.Min(maxHp, hp + 20); }
            }
        });
    }

    void SelectCard(Card card)
    {
        card.apply?.Invoke();
        activeCards = null;
        state = RunState.Playing;
        TryLevelUp();
    }

    void CheckRunEnd()
    {
        if (state == RunState.Playing && !bossSpawned && elapsed >= RunDuration)
        {
            bossSpawned = true;
            SpawnEnemy(EnemyKind.Boss, true);
        }
    }

    void OnGUI()
    {
        GUI.skin.label.richText = true;
        GUI.skin.button.fontSize = 18;
        GUI.skin.label.fontSize = 18;
        GUI.color = Color.white;

        if (state == RunState.Title)
        {
            DrawOverlay(new Color(.025f, .035f, .065f, .93f));
            CenterLabel("잔광의 생존자", 48, new Rect(0, Screen.height * .22f, Screen.width, 70));
            CenterLabel("적을 피하고 성장해 5분 뒤 균열 수호자를 쓰러뜨리세요", 20, new Rect(0, Screen.height * .36f, Screen.width, 42));
            if (GUI.Button(new Rect(Screen.width / 2 - 115, Screen.height * .52f, 230, 58), "게임 시작"))
                state = RunState.Playing;
            CenterLabel("이동: WASD / 방향키     일시정지: Esc     공격: 자동", 17, new Rect(0, Screen.height * .67f, Screen.width, 36));
            return;
        }

        DrawHud();
        if (hitFlash > 0) GUI.color = new Color(1f, .25f, .25f, hitFlash * 1.6f);
        if (state == RunState.LevelUp)
        {
            DrawOverlay(new Color(.015f, .025f, .05f, .84f));
            CenterLabel("레벨 업!  보상을 선택하세요", 34, new Rect(0, Screen.height * .22f, Screen.width, 58));
            List<Card> cards = activeCards ?? new List<Card>();
            float cardW = Mathf.Min(260, (Screen.width - 80) / Mathf.Max(1, cards.Count));
            float totalW = cardW * cards.Count + 18 * (cards.Count - 1);
            float startX = (Screen.width - totalW) / 2;
            for (int i = 0; i < cards.Count; i++)
            {
                Rect r = new Rect(startX + i * (cardW + 18), Screen.height * .37f, cardW, 210);
                if (GUI.Button(r, cards[i].key + "\n\n" + cards[i].title + "\n\n" + cards[i].description + "\n\n[" + (i + 1) + "]"))
                    SelectCard(cards[i]);
            }
        }
        else if (state == RunState.Paused)
        {
            DrawOverlay(new Color(.015f, .025f, .05f, .82f));
            CenterLabel("일시정지", 38, new Rect(0, Screen.height * .3f, Screen.width, 55));
            if (GUI.Button(new Rect(Screen.width / 2 - 110, Screen.height * .48f, 220, 55), "계속하기 (Esc)")) state = RunState.Playing;
        }
        else if (state == RunState.Result)
        {
            DrawOverlay(new Color(.015f, .025f, .05f, .88f));
            CenterLabel(resultHeading, 38, new Rect(0, Screen.height * .22f, Screen.width, 60));
            CenterLabel("생존 " + FormatTime(elapsed) + "   처치 " + kills + "   도달 레벨 " + level, 21, new Rect(0, Screen.height * .36f, Screen.width, 42));
            if (GUI.Button(new Rect(Screen.width / 2 - 220, Screen.height * .52f, 200, 55), "다시 하기")) { ResetRun(); state = RunState.Playing; }
            if (GUI.Button(new Rect(Screen.width / 2 + 20, Screen.height * .52f, 200, 55), "타이틀로")) ResetRun();
        }
        GUI.color = Color.white;
    }

    void DrawHud()
    {
        float timeLeft = Mathf.Max(0, RunDuration - elapsed);
        GUI.Box(new Rect(18, 18, 310, 126), GUIContent.none);
        GUI.Label(new Rect(32, 24, 280, 26), "잔광의 생존자   Lv." + level);
        DrawBar(new Rect(32, 56, 280, 19), hp / maxHp, new Color(.9f, .2f, .3f), "HP " + Mathf.CeilToInt(hp) + " / " + Mathf.CeilToInt(maxHp));
        DrawBar(new Rect(32, 84, 280, 16), xp / xpToNext, new Color(.25f, .8f, 1f), "EXP " + Mathf.FloorToInt(xp) + " / " + Mathf.FloorToInt(xpToNext));
        GUI.Label(new Rect(32, 108, 180, 28), "처치 " + kills);
        GUI.Box(new Rect(Screen.width / 2 - 82, 18, 164, 42), GUIContent.none);
        GUI.Label(new Rect(Screen.width / 2 - 68, 24, 140, 30), FormatTime(timeLeft), CenteredStyle(24));
        if (bossSpawned && boss != null && boss.go != null)
        {
            DrawBar(new Rect(Screen.width * .25f, 72, Screen.width * .5f, 22), boss.hp / boss.maxHp, new Color(.85f, .12f, .22f), "균열 수호자");
        }
        GUI.Label(new Rect(18, Screen.height - 34, 600, 28), "이동 WASD / 방향키     자동 공격     일시정지 Esc");
        GUI.Label(new Rect(Screen.width - 270, 18, 250, 100), InventoryText());
    }

    string InventoryText()
    {
        string s = "무기  ";
        foreach (var pair in weaponLevels) s += WeaponName(pair.Key) + " Lv" + pair.Value + "   ";
        s += "\n패시브  ";
        foreach (var pair in passiveLevels) s += PassiveName(pair.Key) + " Lv" + pair.Value + "   ";
        return s;
    }

    string WeaponName(WeaponKind kind) => kind == WeaponKind.Fireball ? "화염구" : kind == WeaponKind.Slash ? "참격" : "마력장";
    string PassiveName(PassiveKind kind) => kind == PassiveKind.Power ? "힘" : kind == PassiveKind.Boots ? "질주" : "생명";

    void DrawBar(Rect r, float value, Color color, string label)
    {
        GUI.color = new Color(.12f, .15f, .2f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = color;
        GUI.DrawTexture(new Rect(r.x, r.y, r.width * Mathf.Clamp01(value), r.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(r.x + 5, r.y - 2, r.width - 10, r.height + 4), label, CenteredStyle(13));
    }

    GUIStyle CenteredStyle(int fontSize)
    {
        var style = new GUIStyle(GUI.skin.label);
        style.alignment = TextAnchor.MiddleCenter;
        style.fontSize = fontSize;
        style.fontStyle = FontStyle.Bold;
        style.normal.textColor = Color.white;
        return style;
    }

    void CenterLabel(string text, int size, Rect rect)
    {
        GUI.Label(rect, text, CenteredStyle(size));
    }

    void DrawOverlay(Color color)
    {
        GUI.color = color;
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    string FormatTime(float seconds)
    {
        int total = Mathf.FloorToInt(seconds);
        return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
    }

    void OnDestroy()
    {
        if (pixelTexture != null) Destroy(pixelTexture);
    }
}