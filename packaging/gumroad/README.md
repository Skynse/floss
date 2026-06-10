# Gumroad custom landing page (product `dqmpcx`, slug `floss`)

## Live URL

https://popshuvit.gumroad.com/l/floss

CLI/API commands use the edit-url id `dqmpcx` (`gumroad.com/products/dqmpcx/edit`). The public slug is `floss`.

## Edit & publish

```bash
# Preview sanitizer (fix until buy flow + script survive)
gumroad products page preview dqmpcx ./packaging/gumroad/landing.html --json --no-input

# Publish
gumroad products page publish dqmpcx ./packaging/gumroad/landing.html --json --no-input

# Restore default Gumroad product page
gumroad products page clear dqmpcx --yes --json --no-input
```

Product is **pay-what-you-want** (`customizable_price: true`, min $9.99):

- **I want this** — custom price via `gumroad:checkout` postMessage
- **Quick buy — $9.99** — `data-gumroad-action="buy"` + `data-gumroad-price="9.99"`

**Description copy:** Do **not** use `data-gumroad-field="description"` for formatted text — Gumroad replaces it with the product description **HTML-escaped** (tags stripped → one wall of text). Keep paragraphs/lists as static HTML in `landing.html` (sync from `product-description.html`). Update the Gumroad product description separately for search/receipts:

```bash
gumroad products update dqmpcx --description "$(cat packaging/gumroad/product-description.html)"
gumroad products page publish dqmpcx ./packaging/gumroad/landing.html
```
