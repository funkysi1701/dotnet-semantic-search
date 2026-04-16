#!/usr/bin/env python3
"""
Validate SEO strings in appsettings.json and ensure wwwroot/index.html does not duplicate
home-route SEO (title/description/OG/Twitter/JSON-LD) that Blazor injects via HeadOutlet.

Run from repo root:

    python scripts/check_seo_meta.py
"""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[1]
APPSETTINGS = REPO_ROOT / "code" / "SemanticSearch.Web" / "wwwroot" / "appsettings.json"
INDEX_HTML = REPO_ROOT / "code" / "SemanticSearch.Web" / "wwwroot" / "index.html"

TITLE_LEN = (50, 60)
DESC_LEN = (110, 160)


def _len_in_range(label: str, text: str, bounds: tuple[int, int]) -> None:
    lo, hi = bounds
    n = len(text)
    if not lo <= n <= hi:
        print(f"ERROR: {label} length {n} not in [{lo}, {hi}] inclusive.", file=sys.stderr)
        print(f"  Value: {text!r}", file=sys.stderr)
        sys.exit(1)


def _assert_index_has_no_duplicate_seo(html: str) -> None:
    """Shell index.html must not repeat tags that Home.razor emits into HeadOutlet (head::after)."""
    problems = []
    if re.search(r'<meta\s+name="description"', html, re.IGNORECASE):
        problems.append('meta name="description"')
    if re.search(r'<meta\s+property="og:', html, re.IGNORECASE):
        problems.append("Open Graph (og:*) meta tags")
    if re.search(r'<meta\s+name="twitter:', html, re.IGNORECASE):
        problems.append("Twitter card meta tags")
    if re.search(r'<script\s+type="application/ld\+json"', html, re.IGNORECASE):
        problems.append("JSON-LD script")
    if re.search(r'<link\s+rel="canonical"', html, re.IGNORECASE):
        problems.append('link rel="canonical"')
    if problems:
        print(
            "ERROR: index.html must not include home SEO that Home.razor also injects "
            "(duplicate head tags for crawlers). Remove: "
            + ", ".join(problems),
            file=sys.stderr,
        )
        sys.exit(1)


def main() -> None:
    if not APPSETTINGS.is_file():
        print(f"ERROR: Missing {APPSETTINGS}", file=sys.stderr)
        sys.exit(1)
    if not INDEX_HTML.is_file():
        print(f"ERROR: Missing {INDEX_HTML}", file=sys.stderr)
        sys.exit(1)

    cfg = json.loads(APPSETTINGS.read_text(encoding="utf-8"))
    seo = cfg.get("Seo")
    if not isinstance(seo, dict):
        print("ERROR: appsettings.json must contain a Seo object.", file=sys.stderr)
        sys.exit(1)

    home = seo.get("Home")
    nf = seo.get("NotFound")
    base = seo.get("CanonicalBaseUrl")
    if not isinstance(home, dict) or not isinstance(nf, dict):
        print("ERROR: Seo must include Home and NotFound objects.", file=sys.stderr)
        sys.exit(1)
    if not isinstance(base, str) or not base.strip():
        print("ERROR: Seo.CanonicalBaseUrl must be a non-empty string.", file=sys.stderr)
        sys.exit(1)

    base = base.strip().rstrip("/")
    og_site = seo.get("OgSiteName")
    share_alt = seo.get("ShareImageAlt")
    jsonld_name = seo.get("JsonLdSiteName")
    for label, val in (
        ("Seo.OgSiteName", og_site),
        ("Seo.ShareImageAlt", share_alt),
        ("Seo.JsonLdSiteName", jsonld_name),
    ):
        if not isinstance(val, str) or not val.strip():
            print(f"ERROR: {label} must be a non-empty string.", file=sys.stderr)
            sys.exit(1)
    og_site = og_site.strip()
    share_alt = share_alt.strip()
    jsonld_name = jsonld_name.strip()

    home_title = home.get("PageTitle")
    home_desc = home.get("MetaDescription")
    nf_title = nf.get("PageTitle")
    nf_desc = nf.get("MetaDescription")
    for label, val in (
        ("Seo.Home.PageTitle", home_title),
        ("Seo.Home.MetaDescription", home_desc),
        ("Seo.NotFound.PageTitle", nf_title),
        ("Seo.NotFound.MetaDescription", nf_desc),
    ):
        if not isinstance(val, str) or not val.strip():
            print(f"ERROR: {label} must be a non-empty string.", file=sys.stderr)
            sys.exit(1)

    home_title = home_title.strip()
    home_desc = home_desc.strip()
    nf_title = nf_title.strip()
    nf_desc = nf_desc.strip()

    _len_in_range("Seo.Home.PageTitle", home_title, TITLE_LEN)
    _len_in_range("Seo.Home.MetaDescription", home_desc, DESC_LEN)
    _len_in_range("Seo.NotFound.PageTitle", nf_title, TITLE_LEN)
    _len_in_range("Seo.NotFound.MetaDescription", nf_desc, DESC_LEN)

    html = INDEX_HTML.read_text(encoding="utf-8")
    if 'lang="en-GB"' not in html and "lang='en-GB'" not in html:
        print('ERROR: index.html <html> must use lang="en-GB".', file=sys.stderr)
        sys.exit(1)

    _assert_index_has_no_duplicate_seo(html)

    print("SEO check OK:", APPSETTINGS.relative_to(REPO_ROOT))
    print("  (appsettings lengths; index.html has no duplicate home SEO vs HeadOutlet)")


if __name__ == "__main__":
    main()
