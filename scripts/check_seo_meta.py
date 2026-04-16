#!/usr/bin/env python3
"""
Validate SEO strings and keep wwwroot/index.html in sync with appsettings.json.
Enforced in CI (see .github/workflows). Run from repo root:

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


def _meta_content(html: str, *, name: str | None = None, prop: str | None = None) -> str | None:
    if name:
        m = re.search(
            rf'<meta\s+name="{re.escape(name)}"\s+content="([^"]*)"\s*/>',
            html,
            re.IGNORECASE,
        )
    elif prop:
        m = re.search(
            rf'<meta\s+property="{re.escape(prop)}"\s+content="([^"]*)"\s*/>',
            html,
            re.IGNORECASE,
        )
    else:
        raise ValueError("name or prop")
    return m.group(1) if m else None


def _title_text(html: str) -> str | None:
    m = re.search(r"<title>([^<]*)</title>", html, re.IGNORECASE | re.DOTALL)
    return m.group(1).strip() if m else None


def _ld_json_website(html: str) -> dict:
    m = re.search(
        r'<script\s+type="application/ld\+json"\s*>(.*?)</script>',
        html,
        re.IGNORECASE | re.DOTALL,
    )
    if not m:
        print("ERROR: Missing application/ld+json script in index.html.", file=sys.stderr)
        sys.exit(1)
    raw = m.group(1).strip()
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as e:
        print(f"ERROR: Invalid JSON-LD: {e}", file=sys.stderr)
        sys.exit(1)
    if data.get("@type") != "WebSite":
        print('ERROR: JSON-LD root @type must be "WebSite".', file=sys.stderr)
        sys.exit(1)
    return data


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

    idx_title = _title_text(html)
    if idx_title != home_title:
        print("ERROR: index.html <title> must match Seo.Home.PageTitle.", file=sys.stderr)
        print(f"  index.html: {idx_title!r}", file=sys.stderr)
        print(f"  appsettings: {home_title!r}", file=sys.stderr)
        sys.exit(1)

    d = _meta_content(html, name="description")
    if d != home_desc:
        print("ERROR: meta name=description must match Seo.Home.MetaDescription.", file=sys.stderr)
        sys.exit(1)

    og_title = _meta_content(html, prop="og:title")
    og_desc = _meta_content(html, prop="og:description")
    og_url = _meta_content(html, prop="og:url")
    og_image = _meta_content(html, prop="og:image")
    og_type = _meta_content(html, prop="og:type")
    if og_title != home_title or og_desc != home_desc:
        print("ERROR: og:title and og:description must match home PageTitle and MetaDescription.", file=sys.stderr)
        sys.exit(1)
    expected_url = f"{base}/"
    if og_url != expected_url:
        print("ERROR: og:url must equal Seo.CanonicalBaseUrl with trailing slash.", file=sys.stderr)
        print(f"  expected: {expected_url!r}", file=sys.stderr)
        print(f"  actual:   {og_url!r}", file=sys.stderr)
        sys.exit(1)
    expected_image = f"{base}/favicon.svg"
    if og_image != expected_image:
        print("ERROR: og:image must be CanonicalBaseUrl + /favicon.svg.", file=sys.stderr)
        sys.exit(1)
    if (og_type or "").lower() != "website":
        print('ERROR: og:type must be "website".', file=sys.stderr)
        sys.exit(1)
    og_site_meta = _meta_content(html, prop="og:site_name")
    og_img_alt = _meta_content(html, prop="og:image:alt")
    og_loc = _meta_content(html, prop="og:locale")
    if og_site_meta != og_site:
        print("ERROR: og:site_name must match Seo.OgSiteName in appsettings.json.", file=sys.stderr)
        sys.exit(1)
    if og_img_alt != share_alt:
        print("ERROR: og:image:alt must match Seo.ShareImageAlt.", file=sys.stderr)
        sys.exit(1)
    if (og_loc or "").replace("-", "_") != "en_GB":
        print('ERROR: og:locale must be en_GB (hyphen or underscore accepted in file).', file=sys.stderr)
        sys.exit(1)

    tw_title = _meta_content(html, name="twitter:title")
    tw_desc = _meta_content(html, name="twitter:description")
    tw_card = _meta_content(html, name="twitter:card")
    tw_image = _meta_content(html, name="twitter:image")
    if tw_title != home_title or tw_desc != home_desc:
        print("ERROR: twitter:title and twitter:description must match home strings.", file=sys.stderr)
        sys.exit(1)
    if (tw_card or "").lower() not in ("summary", "summary_large_image"):
        print("ERROR: twitter:card must be summary or summary_large_image.", file=sys.stderr)
        sys.exit(1)
    if tw_image != expected_image:
        print("ERROR: twitter:image must match og:image.", file=sys.stderr)
        sys.exit(1)
    tw_img_alt = _meta_content(html, name="twitter:image:alt")
    if tw_img_alt != share_alt:
        print("ERROR: twitter:image:alt must match Seo.ShareImageAlt.", file=sys.stderr)
        sys.exit(1)

    canon = re.search(r'<link\s+rel="canonical"\s+href="([^"]+)"\s*/>', html, re.IGNORECASE)
    if not canon or canon.group(1) != expected_url:
        print("ERROR: index.html must include a canonical link matching Seo.CanonicalBaseUrl + /.", file=sys.stderr)
        sys.exit(1)

    ld = _ld_json_website(html)
    if ld.get("name") != jsonld_name:
        print("ERROR: JSON-LD WebSite.name must match Seo.JsonLdSiteName.", file=sys.stderr)
        sys.exit(1)
    if ld.get("url") != expected_url:
        print("ERROR: JSON-LD WebSite.url must match og:url.", file=sys.stderr)
        sys.exit(1)
    if ld.get("description") != home_desc:
        print("ERROR: JSON-LD WebSite.description must match Seo.Home.MetaDescription.", file=sys.stderr)
        sys.exit(1)

    print("SEO check OK:", APPSETTINGS.relative_to(REPO_ROOT))


if __name__ == "__main__":
    main()
