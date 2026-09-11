# Browser fixtures

`transaction-share-25.cjs` is a local clipping regression for the long JPEG renderer. Install Playwright in a temporary environment if it is not already available, then run:

```text
node Tests/BrowserFixtures/transaction-share-25.cjs
```

It uses a 900px viewport, creates one 1000px-wide canvas that is taller than the viewport, and verifies all 25 participant blocks, the final summary pixels, and a non-empty `image/jpeg` blob. The fixture never reads or writes `App_Data`.
