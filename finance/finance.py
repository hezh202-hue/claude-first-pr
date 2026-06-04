"""
财务记账软件 - 命令行版本
"""
import json
import csv
import os
from datetime import datetime, date
from collections import defaultdict


DATA_FILE = os.path.join(os.path.dirname(__file__), "records.json")

CATEGORIES_INCOME = ["工资", "奖金", "理财", "兼职", "其他收入"]
CATEGORIES_EXPENSE = ["餐饮", "交通", "购物", "娱乐", "医疗", "住房", "教育", "其他支出"]


# ── 数据层 ──────────────────────────────────────────────────────────────────

def load_records() -> list:
    if not os.path.exists(DATA_FILE):
        return []
    with open(DATA_FILE, "r", encoding="utf-8") as f:
        return json.load(f)


def save_records(records: list):
    with open(DATA_FILE, "w", encoding="utf-8") as f:
        json.dump(records, f, ensure_ascii=False, indent=2)


def add_record(kind: str, amount: float, category: str, note: str) -> dict:
    record = {
        "id": datetime.now().strftime("%Y%m%d%H%M%S%f"),
        "date": date.today().isoformat(),
        "kind": kind,          # "收入" or "支出"
        "amount": amount,
        "category": category,
        "note": note,
    }
    records = load_records()
    records.append(record)
    save_records(records)
    return record


# ── 统计层 ──────────────────────────────────────────────────────────────────

def summary_by_month(records: list, year: int, month: int) -> dict:
    income_total = 0.0
    expense_total = 0.0
    income_by_cat: dict[str, float] = defaultdict(float)
    expense_by_cat: dict[str, float] = defaultdict(float)

    for r in records:
        y, m, _ = r["date"].split("-")
        if int(y) == year and int(m) == month:
            if r["kind"] == "收入":
                income_total += r["amount"]
                income_by_cat[r["category"]] += r["amount"]
            else:
                expense_total += r["amount"]
                expense_by_cat[r["category"]] += r["amount"]

    return {
        "income_total": income_total,
        "expense_total": expense_total,
        "net": income_total - expense_total,
        "income_by_cat": dict(income_by_cat),
        "expense_by_cat": dict(expense_by_cat),
    }


def summary_all_time(records: list) -> tuple[float, float]:
    income = sum(r["amount"] for r in records if r["kind"] == "收入")
    expense = sum(r["amount"] for r in records if r["kind"] == "支出")
    return income, expense


# ── 显示层 ──────────────────────────────────────────────────────────────────

def divider(title=""):
    width = 50
    if title:
        pad = (width - len(title) - 2) // 2
        print("─" * pad + f" {title} " + "─" * pad)
    else:
        print("─" * width)


def print_record(r: dict):
    sign = "+" if r["kind"] == "收入" else "-"
    print(f"  {r['date']}  {r['kind']}  {r['category']:<8}  {sign}{r['amount']:>10.2f}  {r['note']}")


def print_category_breakdown(label: str, by_cat: dict):
    if not by_cat:
        return
    print(f"\n  {label}分类明细：")
    for cat, amt in sorted(by_cat.items(), key=lambda x: -x[1]):
        print(f"    {cat:<10} ¥{amt:>10.2f}")


# ── 交互层 ──────────────────────────────────────────────────────────────────

def choose(prompt: str, options: list[str]) -> str:
    print(prompt)
    for i, opt in enumerate(options, 1):
        print(f"  {i}. {opt}")
    while True:
        raw = input("请输入序号：").strip()
        if raw.isdigit() and 1 <= int(raw) <= len(options):
            return options[int(raw) - 1]
        print("  输入无效，请重新选择。")


def input_amount() -> float:
    while True:
        raw = input("金额（元）：").strip()
        try:
            amount = float(raw)
            if amount <= 0:
                raise ValueError
            return round(amount, 2)
        except ValueError:
            print("  请输入正数金额，例如：88.5")


def cmd_add():
    divider("记一笔")
    kind = choose("类型：", ["收入", "支出"])
    cats = CATEGORIES_INCOME if kind == "收入" else CATEGORIES_EXPENSE
    category = choose("分类：", cats)
    amount = input_amount()
    note = input("备注（可留空）：").strip() or "—"
    record = add_record(kind, amount, category, note)
    print(f"\n  ✓ 已记录：{record['date']} {kind} {category} ¥{amount:.2f}")


def cmd_list():
    divider("收支明细")
    records = load_records()
    if not records:
        print("  暂无记录。")
        return

    print(f"  {'日期':<12}{'类型':<6}{'分类':<10}{'金额':>12}  备注")
    divider()
    for r in sorted(records, key=lambda x: x["date"], reverse=True)[:50]:
        print_record(r)

    income, expense = summary_all_time(records)
    divider()
    print(f"  累计收入 ¥{income:>10.2f}    累计支出 ¥{expense:>10.2f}    结余 ¥{income - expense:>10.2f}")


def cmd_monthly():
    divider("月度报告")
    raw = input("查询年月（如 2026-06，留空为本月）：").strip()
    if not raw:
        today = date.today()
        year, month = today.year, today.month
    else:
        try:
            year, month = int(raw.split("-")[0]), int(raw.split("-")[1])
        except Exception:
            print("  格式错误，请输入如 2026-06 的格式。")
            return

    records = load_records()
    stat = summary_by_month(records, year, month)

    print(f"\n  {year} 年 {month} 月")
    divider()
    print(f"  收入合计  ¥{stat['income_total']:>10.2f}")
    print(f"  支出合计  ¥{stat['expense_total']:>10.2f}")
    print(f"  本月结余  ¥{stat['net']:>10.2f}")
    print_category_breakdown("收入", stat["income_by_cat"])
    print_category_breakdown("支出", stat["expense_by_cat"])


def cmd_export():
    divider("导出 CSV")
    records = load_records()
    if not records:
        print("  暂无记录，无需导出。")
        return

    filename = f"finance_export_{date.today().isoformat()}.csv"
    out_path = os.path.join(os.path.dirname(__file__), filename)
    with open(out_path, "w", newline="", encoding="utf-8-sig") as f:
        writer = csv.DictWriter(f, fieldnames=["id", "date", "kind", "amount", "category", "note"])
        writer.writeheader()
        writer.writerows(records)

    print(f"  ✓ 已导出 {len(records)} 条记录到：{out_path}")


def cmd_delete():
    divider("删除记录")
    records = load_records()
    if not records:
        print("  暂无记录。")
        return

    recent = sorted(records, key=lambda x: x["date"], reverse=True)[:10]
    print("  最近 10 条记录：")
    for i, r in enumerate(recent, 1):
        sign = "+" if r["kind"] == "收入" else "-"
        print(f"  {i:>2}. {r['date']}  {r['kind']}  {r['category']:<8}  {sign}{r['amount']:>8.2f}  {r['note']}")

    raw = input("\n输入序号删除（留空取消）：").strip()
    if not raw:
        return
    if raw.isdigit() and 1 <= int(raw) <= len(recent):
        target = recent[int(raw) - 1]
        records = [r for r in records if r["id"] != target["id"]]
        save_records(records)
        print(f"  ✓ 已删除：{target['date']} {target['kind']} ¥{target['amount']:.2f}")
    else:
        print("  输入无效，已取消。")


# ── 主菜单 ──────────────────────────────────────────────────────────────────

MENU = {
    "1": ("记一笔", cmd_add),
    "2": ("查明细", cmd_list),
    "3": ("月度报告", cmd_monthly),
    "4": ("导出CSV", cmd_export),
    "5": ("删除记录", cmd_delete),
    "0": ("退出", None),
}


def main():
    print("\n╔══════════════════════════════════════╗")
    print("║          💰 个人财务记账软件          ║")
    print("╚══════════════════════════════════════╝")

    while True:
        print()
        divider("主菜单")
        for key, (label, _) in MENU.items():
            print(f"  {key}. {label}")

        choice = input("\n请选择：").strip()
        print()

        if choice == "0":
            print("  再见！")
            break
        elif choice in MENU:
            _, func = MENU[choice]
            try:
                func()
            except KeyboardInterrupt:
                print("\n  已取消。")
        else:
            print("  无效选项，请重新输入。")


if __name__ == "__main__":
    main()
