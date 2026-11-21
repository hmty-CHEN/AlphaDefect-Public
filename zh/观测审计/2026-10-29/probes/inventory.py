"""只读扫描现有游戏源码，生成审计目录内的实体索引；不运行或修改游戏。"""
from pathlib import Path
import csv
import hashlib
import json
import re

OUT = Path(__file__).resolve().parents[1]
SRC = Path('/home/chenqiyue/.local/share/sls2/decompiled/v0.111.0')
CATEGORIES = ('Cards', 'Powers', 'Relics', 'Potions', 'Enchantments',
              'Afflictions', 'Orbs', 'Monsters', 'Events')
RULES = {
    'turn_end': r'BeforeTurnEnd|AfterSideTurnEnd|BeforeSideTurnEnd|OnTurnEndInHand',
    'turn_start': r'AfterTurnStart|AfterPlayerTurnStart|AfterSideTurnStart|BeforeSideTurnStart|AfterEnergyReset',
    'damage_modifier': r'ModifyDamage|ModifyHpLost|BeforeDamageReceived|AfterDamageReceived',
    'block_modifier': r'ModifyBlock|GainBlock|ShouldClearBlock',
    'card_selection': r'CardSelectCmd\.|CardSelectorPrefs',
    'replay_count': r'BaseReplayCount|ModifyCardPlayCount|EnchantPlayCount',
    'cost_duration': r'SetThisTurn|SetUntilPlayed|SetThisCombat|AddThisTurn|AddUntilPlayed|AddThisCombat',
    'temporary_keywords': r'RetainThisTurn|SlyThisTurn|ShouldRetain|ShouldFlush',
    'entity_relation': r'Applier|CardSource|TargetCard|IReadOnlyList<CardModel>|List<CardModel>|CardModel\?',
    'custom_state': r'SavedProperty|private (?:bool|int|decimal|CardModel|Creature|ModelId)',
    'orb_effect': r'OrbCmd|ModifyOrb|OrbQueue|PassiveVal|EvokeVal',
    'death_phase': r'AfterDeath|BeforeDeath|ShouldDie|Respawn|Revive|Invulnerable|HardenedShell',
    'draw_pile': r'PileType\.Draw|DrawPile|Shuffle|CardPilePosition\.Top',
    'random_effect': r'NextItem|NextInt|\.Rng|Random',
}

def main():
    rows = []
    for category in CATEGORIES:
        for p in sorted((SRC / ('MegaCrit.Sts2.Core.Models.' + category)).glob('*.cs')):
            s = p.read_text()
            tags = [name for name, regex in RULES.items() if re.search(regex, s)]
            hooks = sorted(set(re.findall(r'override\s+(?:async\s+)?[\w<>?,]+\s+(\w+)\s*\(', s)))
            rows.append(dict(category=category, entity=p.stem, source=str(p),
                             sha256=hashlib.sha256(p.read_bytes()).hexdigest(),
                             tags=';'.join(tags), hooks=';'.join(hooks),
                             review='静态规则已扫描；具体结论见 checklist.json/state_checks.json'))
    with (OUT / 'entity_inventory.csv').open('w', newline='', encoding='utf-8-sig') as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0])); w.writeheader(); w.writerows(rows)
    summary = {'total': len(rows),
               'categories': {c: sum(r['category'] == c for r in rows) for c in CATEGORIES},
               'tag_counts': {k: sum(k in r['tags'].split(';') for r in rows) for k in RULES},
               'limitation': '规则命中只用于筛选。索引不等于每个实体已逐局验证，未命中也不等于没有缺陷。'}
    (OUT / 'inventory_summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps(summary, ensure_ascii=False))

if __name__ == '__main__':
    main()
