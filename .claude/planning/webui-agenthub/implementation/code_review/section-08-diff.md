diff --git a/src/Content/Presentation/Presentation.WebUI/.env.example b/src/Content/Presentation/Presentation.WebUI/.env.example
new file mode 100644
index 0000000..3eecd54
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/.env.example
@@ -0,0 +1,3 @@
+VITE_AZURE_CLIENT_ID=your-spa-app-client-id
+VITE_AZURE_TENANT_ID=your-tenant-id
+VITE_API_BASE_URL=http://localhost:5001
diff --git a/src/Content/Presentation/Presentation.WebUI/package-lock.json b/src/Content/Presentation/Presentation.WebUI/package-lock.json
index 10805e6..5fc62f2 100644
--- a/src/Content/Presentation/Presentation.WebUI/package-lock.json
+++ b/src/Content/Presentation/Presentation.WebUI/package-lock.json
@@ -19,13 +19,12 @@
         "axios": "^1.15.0",
         "class-variance-authority": "^0.7.1",
         "clsx": "^2.1.1",
-        "concurrently": "^9.2.1",
         "lucide-react": "^1.8.0",
         "react": "^19.2.4",
         "react-dom": "^19.2.4",
         "react-hook-form": "^7.72.1",
+        "react-router-dom": "^7.14.1",
         "react-window": "^2.2.7",
-        "shadcn": "^4.2.0",
         "tailwind-merge": "^3.5.0",
         "tailwindcss": "^4.2.2",
         "tw-animate-css": "^1.4.0",
@@ -43,12 +42,14 @@
         "@types/react-window": "^1.8.8",
         "@vitejs/plugin-react": "^6.0.1",
         "@vitest/coverage-v8": "^4.1.4",
+        "concurrently": "^9.2.1",
         "eslint": "^9.39.4",
         "eslint-plugin-react-hooks": "^7.0.1",
         "eslint-plugin-react-refresh": "^0.5.2",
         "globals": "^17.4.0",
         "jsdom": "^29.0.2",
         "msw": "^2.13.3",
+        "shadcn": "^4.2.0",
         "typescript": "~6.0.2",
         "typescript-eslint": "^8.58.0",
         "vite": "^8.0.4",
@@ -139,6 +140,7 @@
       "version": "7.29.0",
       "resolved": "https://registry.npmjs.org/@babel/code-frame/-/code-frame-7.29.0.tgz",
       "integrity": "sha512-9NhCeYjq9+3uxgdtp20LSiJXJvN0FeCtNGpJxuMFZ1Kv3cWUNb6DOhJwUvcVCzKGR66cw4njwM6hrJLqgOwbcw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/helper-validator-identifier": "^7.28.5",
@@ -153,6 +155,7 @@
       "version": "7.29.0",
       "resolved": "https://registry.npmjs.org/@babel/compat-data/-/compat-data-7.29.0.tgz",
       "integrity": "sha512-T1NCJqT/j9+cn8fvkt7jtwbLBfLC/1y1c7NtCeXFRgzGTsafi68MRv8yzkYSapBnFA6L3U2VSc02ciDzoAJhJg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6.9.0"
@@ -162,6 +165,7 @@
       "version": "7.29.0",
       "resolved": "https://registry.npmjs.org/@babel/core/-/core-7.29.0.tgz",
       "integrity": "sha512-CGOfOJqWjg2qW/Mb6zNsDm+u5vFQ8DxXfbM09z69p5Z6+mE1ikP2jUXw+j42Pf1XTYED2Rni5f95npYeuwMDQA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/code-frame": "^7.29.0",
@@ -192,6 +196,7 @@
       "version": "7.29.1",
       "resolved": "https://registry.npmjs.org/@babel/generator/-/generator-7.29.1.tgz",
       "integrity": "sha512-qsaF+9Qcm2Qv8SRIMMscAvG4O3lJ0F1GuMo5HR/Bp02LopNgnZBC/EkbevHFeGs4ls/oPz9v+Bsmzbkbe+0dUw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/parser": "^7.29.0",
@@ -208,6 +213,7 @@
       "version": "7.27.3",
       "resolved": "https://registry.npmjs.org/@babel/helper-annotate-as-pure/-/helper-annotate-as-pure-7.27.3.tgz",
       "integrity": "sha512-fXSwMQqitTGeHLBC08Eq5yXz2m37E4pJX1qAU1+2cNedz/ifv/bVXft90VeSav5nFO61EcNgwr0aJxbyPaWBPg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/types": "^7.27.3"
@@ -220,6 +226,7 @@
       "version": "7.28.6",
       "resolved": "https://registry.npmjs.org/@babel/helper-compilation-targets/-/helper-compilation-targets-7.28.6.tgz",
       "integrity": "sha512-JYtls3hqi15fcx5GaSNL7SCTJ2MNmjrkHXg4FSpOA/grxK8KwyZ5bubHsCq8FXCkua6xhuaaBit+3b7+VZRfcA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/compat-data": "^7.28.6",
@@ -236,6 +243,7 @@
       "version": "7.28.6",
       "resolved": "https://registry.npmjs.org/@babel/helper-create-class-features-plugin/-/helper-create-class-features-plugin-7.28.6.tgz",
       "integrity": "sha512-dTOdvsjnG3xNT9Y0AUg1wAl38y+4Rl4sf9caSQZOXdNqVn+H+HbbJ4IyyHaIqNR6SW9oJpA/RuRjsjCw2IdIow==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/helper-annotate-as-pure": "^7.27.3",
@@ -257,6 +265,7 @@
       "version": "7.28.0",
       "resolved": "https://registry.npmjs.org/@babel/helper-globals/-/helper-globals-7.28.0.tgz",
       "integrity": "sha512-+W6cISkXFa1jXsDEdYA8HeevQT/FULhxzR99pxphltZcVaugps53THCeiWA8SguxxpSp3gKPiuYfSWopkLQ4hw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6.9.0"
@@ -266,6 +275,7 @@
       "version": "7.28.5",
       "resolved": "https://registry.npmjs.org/@babel/helper-member-expression-to-functions/-/helper-member-expression-to-functions-7.28.5.tgz",
       "integrity": "sha512-cwM7SBRZcPCLgl8a7cY0soT1SptSzAlMH39vwiRpOQkJlh53r5hdHwLSCZpQdVLT39sZt+CRpNwYG4Y2v77atg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/traverse": "^7.28.5",
@@ -279,6 +289,7 @@
       "version": "7.28.6",
       "resolved": "https://registry.npmjs.org/@babel/helper-module-imports/-/helper-module-imports-7.28.6.tgz",
       "integrity": "sha512-l5XkZK7r7wa9LucGw9LwZyyCUscb4x37JWTPz7swwFE/0FMQAGpiWUZn8u9DzkSBWEcK25jmvubfpw2dnAMdbw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/traverse": "^7.28.6",
@@ -292,6 +303,7 @@
       "version": "7.28.6",
       "resolved": "https://registry.npmjs.org/@babel/helper-module-transforms/-/helper-module-transforms-7.28.6.tgz",
       "integrity": "sha512-67oXFAYr2cDLDVGLXTEABjdBJZ6drElUSI7WKp70NrpyISso3plG9SAGEF6y7zbha/wOzUByWWTJvEDVNIUGcA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/helper-module-imports": "^7.28.6",
@@ -309,6 +321,7 @@
       "version": "7.27.1",
       "resolved": "https://registry.npmjs.org/@babel/helper-optimise-call-expression/-/helper-optimise-call-expression-7.27.1.tgz",
       "integrity": "sha512-URMGH08NzYFhubNSGJrpUEphGKQwMQYBySzat5cAByY1/YgIRkULnIy3tAMeszlL/so2HbeilYloUmSpd7GdVw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/types": "^7.27.1"
@@ -321,6 +334,7 @@
       "version": "7.28.6",
       "resolved": "https://registry.npmjs.org/@babel/helper-plugin-utils/-/helper-plugin-utils-7.28.6.tgz",
       "integrity": "sha512-S9gzZ/bz83GRysI7gAD4wPT/AI3uCnY+9xn+Mx/KPs2JwHJIz1W8PZkg2cqyt3RNOBM8ejcXhV6y8Og7ly/Dug==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6.9.0"
@@ -330,6 +344,7 @@
       "version": "7.28.6",
       "resolved": "https://registry.npmjs.org/@babel/helper-replace-supers/-/helper-replace-supers-7.28.6.tgz",
       "integrity": "sha512-mq8e+laIk94/yFec3DxSjCRD2Z0TAjhVbEJY3UQrlwVo15Lmt7C2wAUbK4bjnTs4APkwsYLTahXRraQXhb1WCg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/helper-member-expression-to-functions": "^7.28.5",
@@ -347,6 +362,7 @@
       "version": "7.27.1",
       "resolved": "https://registry.npmjs.org/@babel/helper-skip-transparent-expression-wrappers/-/helper-skip-transparent-expression-wrappers-7.27.1.tgz",
       "integrity": "sha512-Tub4ZKEXqbPjXgWLl2+3JpQAYBJ8+ikpQ2Ocj/q/r0LwE3UhENh7EUabyHjz2kCEsrRY83ew2DQdHluuiDQFzg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/traverse": "^7.27.1",
@@ -360,6 +376,7 @@
       "version": "7.27.1",
       "resolved": "https://registry.npmjs.org/@babel/helper-string-parser/-/helper-string-parser-7.27.1.tgz",
       "integrity": "sha512-qMlSxKbpRlAridDExk92nSobyDdpPijUq2DW6oDnUqd0iOGxmQjyqhMIihI9+zv4LPyZdRje2cavWPbCbWm3eA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6.9.0"
@@ -369,6 +386,7 @@
       "version": "7.28.5",
       "resolved": "https://registry.npmjs.org/@babel/helper-validator-identifier/-/helper-validator-identifier-7.28.5.tgz",
       "integrity": "sha512-qSs4ifwzKJSV39ucNjsvc6WVHs6b7S03sOh2OcHF9UHfVPqWWALUsNUVzhSBiItjRZoLHx7nIarVjqKVusUZ1Q==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6.9.0"
@@ -378,6 +396,7 @@
       "version": "7.27.1",
       "resolved": "https://registry.npmjs.org/@babel/helper-validator-option/-/helper-validator-option-7.27.1.tgz",
       "integrity": "sha512-YvjJow9FxbhFFKDSuFnVCe2WxXk1zWc22fFePVNEaWJEu8IrZVlda6N0uHwzZrUM1il7NC9Mlp4MaJYbYd9JSg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6.9.0"
@@ -387,6 +406,7 @@
       "version": "7.29.2",
       "resolved": "https://registry.npmjs.org/@babel/helpers/-/helpers-7.29.2.tgz",
       "integrity": "sha512-HoGuUs4sCZNezVEKdVcwqmZN8GoHirLUcLaYVNBK2J0DadGtdcqgr3BCbvH8+XUo4NGjNl3VOtSjEKNzqfFgKw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/template": "^7.28.6",
@@ -400,6 +420,7 @@
       "version": "7.29.2",
       "resolved": "https://registry.npmjs.org/@babel/parser/-/parser-7.29.2.tgz",
       "integrity": "sha512-4GgRzy/+fsBa72/RZVJmGKPmZu9Byn8o4MoLpmNe1m8ZfYnz5emHLQz3U4gLud6Zwl0RZIcgiLD7Uq7ySFuDLA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/types": "^7.29.0"
@@ -415,6 +436,7 @@
       "version": "7.28.6",
       "resolved": "https://registry.npmjs.org/@babel/plugin-syntax-jsx/-/plugin-syntax-jsx-7.28.6.tgz",
       "integrity": "sha512-wgEmr06G6sIpqr8YDwA2dSRTE3bJ+V0IfpzfSY3Lfgd7YWOaAdlykvJi13ZKBt8cZHfgH1IXN+CL656W3uUa4w==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/helper-plugin-utils": "^7.28.6"
@@ -430,6 +452,7 @@
       "version": "7.28.6",
       "resolved": "https://registry.npmjs.org/@babel/plugin-syntax-typescript/-/plugin-syntax-typescript-7.28.6.tgz",
       "integrity": "sha512-+nDNmQye7nlnuuHDboPbGm00Vqg3oO8niRRL27/4LYHUsHYh0zJ1xWOz0uRwNFmM1Avzk8wZbc6rdiYhomzv/A==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/helper-plugin-utils": "^7.28.6"
@@ -445,6 +468,7 @@
       "version": "7.28.6",
       "resolved": "https://registry.npmjs.org/@babel/plugin-transform-modules-commonjs/-/plugin-transform-modules-commonjs-7.28.6.tgz",
       "integrity": "sha512-jppVbf8IV9iWWwWTQIxJMAJCWBuuKx71475wHwYytrRGQ2CWiDvYlADQno3tcYpS/T2UUWFQp3nVtYfK/YBQrA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/helper-module-transforms": "^7.28.6",
@@ -461,6 +485,7 @@
       "version": "7.28.6",
       "resolved": "https://registry.npmjs.org/@babel/plugin-transform-typescript/-/plugin-transform-typescript-7.28.6.tgz",
       "integrity": "sha512-0YWL2RFxOqEm9Efk5PvreamxPME8OyY0wM5wh5lHjF+VtVhdneCWGzZeSqzOfiobVqQaNCd2z0tQvnI9DaPWPw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/helper-annotate-as-pure": "^7.27.3",
@@ -480,6 +505,7 @@
       "version": "7.28.5",
       "resolved": "https://registry.npmjs.org/@babel/preset-typescript/-/preset-typescript-7.28.5.tgz",
       "integrity": "sha512-+bQy5WOI2V6LJZpPVxY+yp66XdZ2yifu0Mc1aP5CQKgjn4QM5IN2i5fAZ4xKop47pr8rpVhiAeu+nDQa12C8+g==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/helper-plugin-utils": "^7.27.1",
@@ -508,6 +534,7 @@
       "version": "7.28.6",
       "resolved": "https://registry.npmjs.org/@babel/template/-/template-7.28.6.tgz",
       "integrity": "sha512-YA6Ma2KsCdGb+WC6UpBVFJGXL58MDA6oyONbjyF/+5sBgxY/dwkhLogbMT2GXXyU84/IhRw/2D1Os1B/giz+BQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/code-frame": "^7.28.6",
@@ -522,6 +549,7 @@
       "version": "7.29.0",
       "resolved": "https://registry.npmjs.org/@babel/traverse/-/traverse-7.29.0.tgz",
       "integrity": "sha512-4HPiQr0X7+waHfyXPZpWPfWL/J7dcN1mx9gL6WdQVMbPnF3+ZhSMs8tCxN7oHddJE9fhNE7+lxdnlyemKfJRuA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/code-frame": "^7.29.0",
@@ -540,6 +568,7 @@
       "version": "7.29.0",
       "resolved": "https://registry.npmjs.org/@babel/types/-/types-7.29.0.tgz",
       "integrity": "sha512-LwdZHpScM4Qz8Xw2iKSzS+cfglZzJGvofQICy7W7v4caru4EaAmyUuO6BGrbyQ2mYV11W0U8j5mBhd14dd3B0A==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/helper-string-parser": "^7.27.1",
@@ -777,6 +806,7 @@
       "version": "1.61.0",
       "resolved": "https://registry.npmjs.org/@dotenvx/dotenvx/-/dotenvx-1.61.0.tgz",
       "integrity": "sha512-utL3cpZoFzflyqUkjYbxYujI6STBTmO5LFn4bbin/NZnRWN6wQ7eErhr3/Vpa5h/jicPFC6kTa42r940mQftJQ==",
+      "dev": true,
       "license": "BSD-3-Clause",
       "dependencies": {
         "commander": "^11.1.0",
@@ -801,6 +831,7 @@
       "version": "11.1.0",
       "resolved": "https://registry.npmjs.org/commander/-/commander-11.1.0.tgz",
       "integrity": "sha512-yPVavfyCcRhmorC7rWlkHn15b4wDVgVmBA7kV4QVBsF7kv/9TKJAbAXVTxvTnwP8HHKjRCJDClKbciiYS7p0DQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=16"
@@ -810,6 +841,7 @@
       "version": "5.1.1",
       "resolved": "https://registry.npmjs.org/execa/-/execa-5.1.1.tgz",
       "integrity": "sha512-8uSpZZocAZRBAPIEINJj3Lo9HyGitllczc27Eh5YYojjMFMn8yHMDMaUHE2Jqfq05D/wucwI4JGURyXt1vchyg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "cross-spawn": "^7.0.3",
@@ -833,6 +865,7 @@
       "version": "6.0.1",
       "resolved": "https://registry.npmjs.org/get-stream/-/get-stream-6.0.1.tgz",
       "integrity": "sha512-ts6Wi+2j3jQjqi70w5AlN8DFnkSwC+MqmxEzdEALB2qXZYV3X/b1CTfgPLGJNMeAWxdPfU8FO1ms3NUfaHCPYg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=10"
@@ -845,6 +878,7 @@
       "version": "2.1.0",
       "resolved": "https://registry.npmjs.org/human-signals/-/human-signals-2.1.0.tgz",
       "integrity": "sha512-B4FFZ6q/T2jhhksgkbEW3HBvWIfDW85snkQgawt07S7J5QXTk6BkNV+0yAeZrM5QpMAdYlocGoljn0sJ/WQkFw==",
+      "dev": true,
       "license": "Apache-2.0",
       "engines": {
         "node": ">=10.17.0"
@@ -854,6 +888,7 @@
       "version": "2.0.1",
       "resolved": "https://registry.npmjs.org/is-stream/-/is-stream-2.0.1.tgz",
       "integrity": "sha512-hFoiJiTl63nn+kstHGBtewWSKnQLpyb155KHheA1l39uvtO9nWIop1p3udqPcUd/xbF1VLMO4n7OI6p7RbngDg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=8"
@@ -866,6 +901,7 @@
       "version": "3.1.5",
       "resolved": "https://registry.npmjs.org/isexe/-/isexe-3.1.5.tgz",
       "integrity": "sha512-6B3tLtFqtQS4ekarvLVMZ+X+VlvQekbe4taUkf/rhVO3d/h0M2rfARm/pXLcPEsjjMsFgrFgSrhQIxcSVrBz8w==",
+      "dev": true,
       "license": "BlueOak-1.0.0",
       "engines": {
         "node": ">=18"
@@ -875,6 +911,7 @@
       "version": "4.0.1",
       "resolved": "https://registry.npmjs.org/npm-run-path/-/npm-run-path-4.0.1.tgz",
       "integrity": "sha512-S48WzZW777zhNIrn7gxOlISNAqi9ZC/uQFnRdbeIHhZhCA6UqpkOT8T1G7BvfdgP4Er8gF4sUbaS0i7QvIfCWw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "path-key": "^3.0.0"
@@ -887,6 +924,7 @@
       "version": "5.1.2",
       "resolved": "https://registry.npmjs.org/onetime/-/onetime-5.1.2.tgz",
       "integrity": "sha512-kbpaSSGJTWdAY5KPVeMOKXSrPtr8C8C7wodJbcsd51jRnmD+GZu8Y0VoU6Dm5Z4vWr0Ig/1NKuWRKf7j5aaYSg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "mimic-fn": "^2.1.0"
@@ -902,12 +940,14 @@
       "version": "3.0.7",
       "resolved": "https://registry.npmjs.org/signal-exit/-/signal-exit-3.0.7.tgz",
       "integrity": "sha512-wnD2ZE+l+SPC/uoS0vXeE9L1+0wuaMqKlfz9AMUo38JsyLSBWSFcHR1Rri62LZc12vLr1gb3jl7iwQhgwpAbGQ==",
+      "dev": true,
       "license": "ISC"
     },
     "node_modules/@dotenvx/dotenvx/node_modules/strip-final-newline": {
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/strip-final-newline/-/strip-final-newline-2.0.0.tgz",
       "integrity": "sha512-BrpvfNAE3dcvq7ll3xVumzjKjZQ5tI1sEUIKr3Uoks0XUl45St3FlatVqef9prk4jRDzhW6WZg+3bk93y6pLjA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6"
@@ -917,6 +957,7 @@
       "version": "4.0.0",
       "resolved": "https://registry.npmjs.org/which/-/which-4.0.0.tgz",
       "integrity": "sha512-GlaYyEb07DPxYCKhKzplCWBJtvxZcZMrL+4UkrTSJHHPyZU4mYYTv3qaOe77H7EODLSSopAUFAc6W8U4yqvscg==",
+      "dev": true,
       "license": "ISC",
       "dependencies": {
         "isexe": "^3.1.1"
@@ -932,6 +973,7 @@
       "version": "0.2.6",
       "resolved": "https://registry.npmjs.org/@ecies/ciphers/-/ciphers-0.2.6.tgz",
       "integrity": "sha512-patgsRPKGkhhoBjETV4XxD0En4ui5fbX0hzayqI3M8tvNMGUoUvmyYAIWwlxBc1KX5cturfqByYdj5bYGRpN9g==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "bun": ">=1",
@@ -1199,6 +1241,7 @@
       "version": "1.19.14",
       "resolved": "https://registry.npmjs.org/@hono/node-server/-/node-server-1.19.14.tgz",
       "integrity": "sha512-GwtvgtXxnWsucXvbQXkRgqksiH2Qed37H9xHZocE5sA3N8O8O8/8FA3uclQXxXVzc9XBZuEOMK7+r02FmSpHtw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18.14.1"
@@ -1275,6 +1318,7 @@
       "version": "1.0.2",
       "resolved": "https://registry.npmjs.org/@inquirer/ansi/-/ansi-1.0.2.tgz",
       "integrity": "sha512-S8qNSZiYzFd0wAcyG5AXCvUHC5Sr7xpZ9wZ2py9XR88jUz8wooStVx5M6dRzczbBWjic9NP7+rY0Xi7qqK/aMQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -1284,6 +1328,7 @@
       "version": "5.1.21",
       "resolved": "https://registry.npmjs.org/@inquirer/confirm/-/confirm-5.1.21.tgz",
       "integrity": "sha512-KR8edRkIsUayMXV+o3Gv+q4jlhENF9nMYUZs9PA2HzrXeHI8M5uDag70U7RJn9yyiMZSbtF5/UexBtAVtZGSbQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@inquirer/core": "^10.3.2",
@@ -1305,6 +1350,7 @@
       "version": "10.3.2",
       "resolved": "https://registry.npmjs.org/@inquirer/core/-/core-10.3.2.tgz",
       "integrity": "sha512-43RTuEbfP8MbKzedNqBrlhhNKVwoK//vUFNW3Q3vZ88BLcrs4kYpGg+B2mm5p2K/HfygoCxuKwJJiv8PbGmE0A==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@inquirer/ansi": "^1.0.2",
@@ -1332,6 +1378,7 @@
       "version": "6.2.0",
       "resolved": "https://registry.npmjs.org/wrap-ansi/-/wrap-ansi-6.2.0.tgz",
       "integrity": "sha512-r6lPcBGxZXlIcymEu7InxDMhdW0KDxpLgoFLcguasxCaJ/SOIZwINatK9KY/tf+ZrlywOKU0UDj3ATXUBfxJXA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "ansi-styles": "^4.0.0",
@@ -1346,6 +1393,7 @@
       "version": "1.0.15",
       "resolved": "https://registry.npmjs.org/@inquirer/figures/-/figures-1.0.15.tgz",
       "integrity": "sha512-t2IEY+unGHOzAaVM5Xx6DEWKeXlDDcNPeDyUpsRc6CUhBfU3VQOEl+Vssh7VNp1dR8MdUJBWhuObjXCsVpjN5g==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -1355,6 +1403,7 @@
       "version": "3.0.10",
       "resolved": "https://registry.npmjs.org/@inquirer/type/-/type-3.0.10.tgz",
       "integrity": "sha512-BvziSRxfz5Ov8ch0z/n3oijRSEcEsHnhggm4xFZe93DHcUCTlutlq9Ox4SVENAfcRD22UQq7T/atg9Wr3k09eA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -1430,6 +1479,7 @@
       "version": "1.29.0",
       "resolved": "https://registry.npmjs.org/@modelcontextprotocol/sdk/-/sdk-1.29.0.tgz",
       "integrity": "sha512-zo37mZA9hJWpULgkRpowewez1y6ML5GsXJPY8FI0tBBCd77HEvza4jDqRKOXgHNn867PVGCyTdzqpz0izu5ZjQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@hono/node-server": "^1.19.9",
@@ -1470,6 +1520,7 @@
       "version": "8.18.0",
       "resolved": "https://registry.npmjs.org/ajv/-/ajv-8.18.0.tgz",
       "integrity": "sha512-PlXPeEWMXMZ7sPYOHqmDyCJzcfNrUr3fGNKtezX14ykXOEIvyK81d+qydx89KY5O71FKMPaQ2vBfBFI5NHR63A==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "fast-deep-equal": "^3.1.3",
@@ -1486,6 +1537,7 @@
       "version": "3.0.7",
       "resolved": "https://registry.npmjs.org/eventsource/-/eventsource-3.0.7.tgz",
       "integrity": "sha512-CRT1WTyuQoD771GW56XEZFQ/ZoSfWid1alKGDYMmkt2yl8UXrVR4pspqWNEcqKvVIzg6PAltWjxcSSPrboA4iA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "eventsource-parser": "^3.0.1"
@@ -1498,12 +1550,14 @@
       "version": "1.0.0",
       "resolved": "https://registry.npmjs.org/json-schema-traverse/-/json-schema-traverse-1.0.0.tgz",
       "integrity": "sha512-NM8/P9n3XjXhIZn1lLhkFaACTOURQXjWhV4BA/RnOv8xvgqtqpAX9IO4mRQxSx1Rlo4tqzeqb0sOlruaOy3dug==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/@mswjs/interceptors": {
       "version": "0.41.3",
       "resolved": "https://registry.npmjs.org/@mswjs/interceptors/-/interceptors-0.41.3.tgz",
       "integrity": "sha512-cXu86tF4VQVfwz8W1SPbhoRyHJkti6mjH/XJIxp40jhO4j2k1m4KYrEykxqWPkFF3vrK4rgQppBh//AwyGSXPA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@open-draft/deferred-promise": "^2.2.0",
@@ -1539,6 +1593,7 @@
       "version": "1.3.0",
       "resolved": "https://registry.npmjs.org/@noble/ciphers/-/ciphers-1.3.0.tgz",
       "integrity": "sha512-2I0gnIVPtfnMw9ee9h1dJG7tp81+8Ob3OJb3Mv37rx5L40/b0i7djjCVvGOVqc9AEIQyvyu1i6ypKdFw8R8gQw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": "^14.21.3 || >=16"
@@ -1551,6 +1606,7 @@
       "version": "1.9.7",
       "resolved": "https://registry.npmjs.org/@noble/curves/-/curves-1.9.7.tgz",
       "integrity": "sha512-gbKGcRUYIjA3/zCCNaWDciTMFI0dCkvou3TL8Zmy5Nc7sJ47a0jtOeZoTaMxkuqRo9cRhjOdZJXegxYE5FN/xw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@noble/hashes": "1.8.0"
@@ -1566,6 +1622,7 @@
       "version": "1.8.0",
       "resolved": "https://registry.npmjs.org/@noble/hashes/-/hashes-1.8.0.tgz",
       "integrity": "sha512-jCs9ldd7NwzpgXDIf6P3+NrHh9/sD6CQdxHyjQI+h/6rDNo88ypBxxz45UDuZHz9r3tNz7N/VInSVoVdtXEI4A==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": "^14.21.3 || >=16"
@@ -1578,6 +1635,7 @@
       "version": "2.1.5",
       "resolved": "https://registry.npmjs.org/@nodelib/fs.scandir/-/fs.scandir-2.1.5.tgz",
       "integrity": "sha512-vq24Bq3ym5HEQm2NKCr3yXDwjc7vTsEThRDnkp2DK9p1uqLR+DHurm/NOTo0KG7HYHU7eppKZj3MyqYuMBf62g==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@nodelib/fs.stat": "2.0.5",
@@ -1591,6 +1649,7 @@
       "version": "2.0.5",
       "resolved": "https://registry.npmjs.org/@nodelib/fs.stat/-/fs.stat-2.0.5.tgz",
       "integrity": "sha512-RkhPPp2zrqDAQA/2jNhnztcPAlv64XdhIp7a7454A5ovI7Bukxgt7MX7udwAu3zg1DcpPU0rz3VV1SeaqvY4+A==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 8"
@@ -1600,6 +1659,7 @@
       "version": "1.2.8",
       "resolved": "https://registry.npmjs.org/@nodelib/fs.walk/-/fs.walk-1.2.8.tgz",
       "integrity": "sha512-oGB+UxlgWcgQkgwo8GcEGwemoTFt3FIO9ababBmaGwXIoBKZ+GTy0pP185beGg7Llih/NSHSV2XAs1lnznocSg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@nodelib/fs.scandir": "2.1.5",
@@ -1613,12 +1673,14 @@
       "version": "2.2.0",
       "resolved": "https://registry.npmjs.org/@open-draft/deferred-promise/-/deferred-promise-2.2.0.tgz",
       "integrity": "sha512-CecwLWx3rhxVQF6V4bAgPS5t+So2sTbPgAzafKkVizyi7tlwpcFpdFqq+wqF2OwNBmqFuu6tOyouTuxgpMfzmA==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/@open-draft/logger": {
       "version": "0.3.0",
       "resolved": "https://registry.npmjs.org/@open-draft/logger/-/logger-0.3.0.tgz",
       "integrity": "sha512-X2g45fzhxH238HKO4xbSr7+wBS8Fvw6ixhTDuvLd5mqh6bJJCFAPwU9mPDxbcrRtfxv4u5IHCEH77BmxvXmmxQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "is-node-process": "^1.2.0",
@@ -1629,6 +1691,7 @@
       "version": "2.1.0",
       "resolved": "https://registry.npmjs.org/@open-draft/until/-/until-2.1.0.tgz",
       "integrity": "sha512-U69T3ItWHvLwGg5eJ0n3I62nWuE6ilHlmz7zM0npLBRvPRd7e6NYmg54vvRtP5mZG7kZqZCFVdsTWo7BPtBujg==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/@oxc-project/types": {
@@ -1893,12 +1956,14 @@
       "version": "0.4.1",
       "resolved": "https://registry.npmjs.org/@sec-ant/readable-stream/-/readable-stream-0.4.1.tgz",
       "integrity": "sha512-831qok9r2t8AlxLko40y2ebgSDhenenCatLVeW/uBtnHPyhHOvG0C7TvfgecV+wHzIm5KUICgzmVpWS+IMEAeg==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/@sindresorhus/merge-streams": {
       "version": "4.0.0",
       "resolved": "https://registry.npmjs.org/@sindresorhus/merge-streams/-/merge-streams-4.0.0.tgz",
       "integrity": "sha512-tlqY9xq5ukxTUZBmoOp+m61cqwQD5pHJtFY3Mn8CA8ps6yghLH/Hw8UPdqg4OLmFW3IFlcXnQNmo/dh8HzXYIQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -2297,6 +2362,7 @@
       "version": "0.27.0",
       "resolved": "https://registry.npmjs.org/@ts-morph/common/-/common-0.27.0.tgz",
       "integrity": "sha512-Wf29UqxWDpc+i61k3oIOzcUfQt79PIT9y/MWfAGlrkjg6lBC1hwDECLXPVJAhWjiGbfBCxZd65F/LIZF3+jeJQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "fast-glob": "^3.3.3",
@@ -2308,6 +2374,7 @@
       "version": "4.0.4",
       "resolved": "https://registry.npmjs.org/balanced-match/-/balanced-match-4.0.4.tgz",
       "integrity": "sha512-BLrgEcRTwX2o6gGxGOCNyMvGSp35YofuYzw9h1IMTRmKqttAZZVU67bdb9Pr2vUHA8+j3i2tJfjO6C6+4myGTA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": "18 || 20 || >=22"
@@ -2317,6 +2384,7 @@
       "version": "5.0.5",
       "resolved": "https://registry.npmjs.org/brace-expansion/-/brace-expansion-5.0.5.tgz",
       "integrity": "sha512-VZznLgtwhn+Mact9tfiwx64fA9erHH/MCXEUfB/0bX/6Fz6ny5EGTXYltMocqg4xFAQZtnO3DHWWXi8RiuN7cQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "balanced-match": "^4.0.2"
@@ -2329,6 +2397,7 @@
       "version": "10.2.5",
       "resolved": "https://registry.npmjs.org/minimatch/-/minimatch-10.2.5.tgz",
       "integrity": "sha512-MULkVLfKGYDFYejP07QOurDLLQpcjk7Fw+7jXS2R2czRQzR56yHRveU5NDJEOviH+hETZKSkIk5c+T23GjFUMg==",
+      "dev": true,
       "license": "BlueOak-1.0.0",
       "dependencies": {
         "brace-expansion": "^5.0.5"
@@ -2434,12 +2503,14 @@
       "version": "2.0.6",
       "resolved": "https://registry.npmjs.org/@types/statuses/-/statuses-2.0.6.tgz",
       "integrity": "sha512-xMAgYwceFhRA2zY+XbEA7mxYbA093wdiW8Vu6gZPGWy9cmOyU9XesH1tNcEWsKFd5Vzrqx5T3D38PWx1FIIXkA==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/@types/validate-npm-package-name": {
       "version": "4.0.2",
       "resolved": "https://registry.npmjs.org/@types/validate-npm-package-name/-/validate-npm-package-name-4.0.2.tgz",
       "integrity": "sha512-lrpDziQipxCEeK5kWxvljWYhUvOiB2A9izZd9B2AFarYAkqZshb4lPbRs7zKEic6eGtH8V/2qJW+dPp9OtF6bw==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/@typescript-eslint/eslint-plugin": {
@@ -2923,6 +2994,7 @@
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/accepts/-/accepts-2.0.0.tgz",
       "integrity": "sha512-5cvg6CtKwfgdmVqY1WIiXKc3Q1bkRqGLi+2W/6ao+6Y7gu/RCwRuAhGEzh5B4KlszSuTLgZYuqFqo5bImjNKng==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "mime-types": "^3.0.0",
@@ -2936,6 +3008,7 @@
       "version": "1.54.0",
       "resolved": "https://registry.npmjs.org/mime-db/-/mime-db-1.54.0.tgz",
       "integrity": "sha512-aU5EJuIN2WDemCcAp2vFBfp/m4EAhWJnUNSSw0ixs7/kXbd6Pg64EmwJkNdFhB8aWt1sH2CTXrLxo/iAGV3oPQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.6"
@@ -2945,6 +3018,7 @@
       "version": "3.0.2",
       "resolved": "https://registry.npmjs.org/mime-types/-/mime-types-3.0.2.tgz",
       "integrity": "sha512-Lbgzdk0h4juoQ9fCKXW4by0UJqj+nOOrI9MJ1sSj4nI8aI2eo1qmvQEie4VD1glsS250n15LsWsYtCugiStS5A==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "mime-db": "^1.54.0"
@@ -2984,6 +3058,7 @@
       "version": "7.1.4",
       "resolved": "https://registry.npmjs.org/agent-base/-/agent-base-7.1.4.tgz",
       "integrity": "sha512-MnA+YT8fwfJPgBx3m60MNqakm30XOkyIoH1y6huTQvC0PwZG7ki8NacLBcrPbNoo8vEZy7Jpuk7+jMO+CUovTQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 14"
@@ -3010,6 +3085,7 @@
       "version": "3.0.1",
       "resolved": "https://registry.npmjs.org/ajv-formats/-/ajv-formats-3.0.1.tgz",
       "integrity": "sha512-8iUql50EUR+uUcdRQ3HDqa6EVyo3docL8g5WJ3FNcWmu62IbkGUue/pEyLBW8VGKKucTPgqeks4fIU1DA4yowQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "ajv": "^8.0.0"
@@ -3027,6 +3103,7 @@
       "version": "8.18.0",
       "resolved": "https://registry.npmjs.org/ajv/-/ajv-8.18.0.tgz",
       "integrity": "sha512-PlXPeEWMXMZ7sPYOHqmDyCJzcfNrUr3fGNKtezX14ykXOEIvyK81d+qydx89KY5O71FKMPaQ2vBfBFI5NHR63A==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "fast-deep-equal": "^3.1.3",
@@ -3043,12 +3120,14 @@
       "version": "1.0.0",
       "resolved": "https://registry.npmjs.org/json-schema-traverse/-/json-schema-traverse-1.0.0.tgz",
       "integrity": "sha512-NM8/P9n3XjXhIZn1lLhkFaACTOURQXjWhV4BA/RnOv8xvgqtqpAX9IO4mRQxSx1Rlo4tqzeqb0sOlruaOy3dug==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/ansi-regex": {
       "version": "5.0.1",
       "resolved": "https://registry.npmjs.org/ansi-regex/-/ansi-regex-5.0.1.tgz",
       "integrity": "sha512-quJQXlTSUGL2LH9SUXo8VwsY4soanhgo6LNSm84E1LBcE8s3O0wpdiRzyR9z/ZZJMlMWv37qOOb9pdJlMUEKFQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=8"
@@ -3058,6 +3137,7 @@
       "version": "4.3.0",
       "resolved": "https://registry.npmjs.org/ansi-styles/-/ansi-styles-4.3.0.tgz",
       "integrity": "sha512-zbB9rCJAT1rbjiVDb2hqKFHNYLxgtk8NURxZ3IZwD3F6NtxbXZQCnnSi1Lkx+IDohdPlFp222wVALIheZJQSEg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "color-convert": "^2.0.1"
@@ -3073,6 +3153,7 @@
       "version": "2.0.1",
       "resolved": "https://registry.npmjs.org/argparse/-/argparse-2.0.1.tgz",
       "integrity": "sha512-8+9WqebbFzpX9OR+Wa6O29asIogeRMzcGtAINdpMHHyAg10f05aSFVBbcEqGf/PXw1EjAZ+q2/bEBg3DvurK3Q==",
+      "dev": true,
       "license": "Python-2.0"
     },
     "node_modules/aria-query": {
@@ -3099,6 +3180,7 @@
       "version": "0.16.1",
       "resolved": "https://registry.npmjs.org/ast-types/-/ast-types-0.16.1.tgz",
       "integrity": "sha512-6t10qk83GOG8p0vKmaCr8eiilZwO171AvbROMtvvNiwrTly62t+7XkA8RdIIVbpMhCASAsxgAzdRSwh6nw/5Dg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "tslib": "^2.0.1"
@@ -3154,6 +3236,7 @@
       "version": "2.10.19",
       "resolved": "https://registry.npmjs.org/baseline-browser-mapping/-/baseline-browser-mapping-2.10.19.tgz",
       "integrity": "sha512-qCkNLi2sfBOn8XhZQ0FXsT1Ki/Yo5P90hrkRamVFRS7/KV9hpfA4HkoWNU152+8w0zPjnxo5psx5NL3PSGgv5g==",
+      "dev": true,
       "license": "Apache-2.0",
       "bin": {
         "baseline-browser-mapping": "dist/cli.cjs"
@@ -3176,6 +3259,7 @@
       "version": "2.2.2",
       "resolved": "https://registry.npmjs.org/body-parser/-/body-parser-2.2.2.tgz",
       "integrity": "sha512-oP5VkATKlNwcgvxi0vM0p/D3n2C3EReYVX+DNYs5TjZFn/oQt2j+4sVJtSMr18pdRr8wjTcBl6LoV+FUwzPmNA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "bytes": "^3.1.2",
@@ -3211,6 +3295,7 @@
       "version": "3.0.3",
       "resolved": "https://registry.npmjs.org/braces/-/braces-3.0.3.tgz",
       "integrity": "sha512-yQbXgO/OSZVD2IsiLlro+7Hf6Q18EJrKSEsdoMzKePKXct3gvD8oLcOQdIzGupr5Fj+EDe8gO/lxc1BzfMpxvA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "fill-range": "^7.1.1"
@@ -3223,6 +3308,7 @@
       "version": "4.28.2",
       "resolved": "https://registry.npmjs.org/browserslist/-/browserslist-4.28.2.tgz",
       "integrity": "sha512-48xSriZYYg+8qXna9kwqjIVzuQxi+KYWp2+5nCYnYKPTr0LvD89Jqk2Or5ogxz0NUMfIjhh2lIUX/LyX9B4oIg==",
+      "dev": true,
       "funding": [
         {
           "type": "opencollective",
@@ -3256,6 +3342,7 @@
       "version": "4.1.0",
       "resolved": "https://registry.npmjs.org/bundle-name/-/bundle-name-4.1.0.tgz",
       "integrity": "sha512-tjwM5exMg6BGRI+kNmTntNsvdZS1X8BFYS6tnJ2hdH0kVxM6/eVZ2xy+FqStSWvYmtfFMDLIxurorHwDKfDz5Q==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "run-applescript": "^7.0.0"
@@ -3271,6 +3358,7 @@
       "version": "3.1.2",
       "resolved": "https://registry.npmjs.org/bytes/-/bytes-3.1.2.tgz",
       "integrity": "sha512-/Nf7TyzTx6S3yRJObOAV7956r8cr2+Oj8AC5dt8wSP3BQAoeX58NoHyCU8P8zGkNXStjTSi6fzO6F0pBdcYbEg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.8"
@@ -3293,6 +3381,7 @@
       "version": "1.0.4",
       "resolved": "https://registry.npmjs.org/call-bound/-/call-bound-1.0.4.tgz",
       "integrity": "sha512-+ys997U96po4Kx/ABpBCqhA9EuxJaQWDQg7295H4hBphv3IZg0boBKuwYpt4YXp6MZ5AmZQnU/tyMTlRpaSejg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "call-bind-apply-helpers": "^1.0.2",
@@ -3309,6 +3398,7 @@
       "version": "3.1.0",
       "resolved": "https://registry.npmjs.org/callsites/-/callsites-3.1.0.tgz",
       "integrity": "sha512-P8BjAsXvZS+VIDUI11hHCQEv74YT67YUi5JJFNWIqL235sBmjX4+qx9Muvls5ivyNENctx46xQLQ3aTuE7ssaQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6"
@@ -3318,6 +3408,7 @@
       "version": "1.0.30001788",
       "resolved": "https://registry.npmjs.org/caniuse-lite/-/caniuse-lite-1.0.30001788.tgz",
       "integrity": "sha512-6q8HFp+lOQtcf7wBK+uEenxymVWkGKkjFpCvw5W25cmMwEDU45p1xQFBQv8JDlMMry7eNxyBaR+qxgmTUZkIRQ==",
+      "dev": true,
       "funding": [
         {
           "type": "opencollective",
@@ -3348,6 +3439,7 @@
       "version": "4.1.2",
       "resolved": "https://registry.npmjs.org/chalk/-/chalk-4.1.2.tgz",
       "integrity": "sha512-oKnbhFyRIXpUuez8iBMmyEa4nbj4IOQyuhc/wy9kY7/WVPcwIO9VA668Pu8RkO7+0G76SLROeyw9CpQ061i4mA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "ansi-styles": "^4.1.0",
@@ -3364,6 +3456,7 @@
       "version": "7.2.0",
       "resolved": "https://registry.npmjs.org/supports-color/-/supports-color-7.2.0.tgz",
       "integrity": "sha512-qpCAvRl9stuOHveKsn7HncJRvv501qIacKzQlO/+Lwxc9+0q2wLyv4Dfvt80/DPn2pqOBsJdDiogXGR9+OvwRw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "has-flag": "^4.0.0"
@@ -3388,6 +3481,7 @@
       "version": "5.0.0",
       "resolved": "https://registry.npmjs.org/cli-cursor/-/cli-cursor-5.0.0.tgz",
       "integrity": "sha512-aCj4O5wKyszjMmDT4tZj93kxyydN/K5zPWSCe6/0AV/AA1pqe5ZBIw0a2ZfPQV7lL5/yb5HsUreJ6UFAF1tEQw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "restore-cursor": "^5.0.0"
@@ -3403,6 +3497,7 @@
       "version": "2.9.2",
       "resolved": "https://registry.npmjs.org/cli-spinners/-/cli-spinners-2.9.2.tgz",
       "integrity": "sha512-ywqV+5MmyL4E7ybXgKys4DugZbX0FC6LnwrhjuykIjnK9k8OQacQ7axGKnjDXWNhns0xot3bZI5h55H8yo9cJg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6"
@@ -3415,6 +3510,7 @@
       "version": "4.1.0",
       "resolved": "https://registry.npmjs.org/cli-width/-/cli-width-4.1.0.tgz",
       "integrity": "sha512-ouuZd4/dm2Sw5Gmqy6bGyNNNe1qt9RpmxveLSO7KcgsTnU7RXfsw+/bukWGo1abgBiMAic068rclZsO4IWmmxQ==",
+      "dev": true,
       "license": "ISC",
       "engines": {
         "node": ">= 12"
@@ -3424,6 +3520,7 @@
       "version": "8.0.1",
       "resolved": "https://registry.npmjs.org/cliui/-/cliui-8.0.1.tgz",
       "integrity": "sha512-BSeNnyus75C4//NQ9gQt1/csTXyo/8Sb+afLAkzAptFuMsod9HFokGNudZpi/oQV73hnVK+sR+5PVRMd+Dr7YQ==",
+      "dev": true,
       "license": "ISC",
       "dependencies": {
         "string-width": "^4.2.0",
@@ -3447,12 +3544,14 @@
       "version": "13.0.3",
       "resolved": "https://registry.npmjs.org/code-block-writer/-/code-block-writer-13.0.3.tgz",
       "integrity": "sha512-Oofo0pq3IKnsFtuHqSF7TqBfr71aeyZDVJ0HpmqB7FBM2qEigL0iPONSCZSO9pE9dZTAxANe5XHG9Uy0YMv8cg==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/color-convert": {
       "version": "2.0.1",
       "resolved": "https://registry.npmjs.org/color-convert/-/color-convert-2.0.1.tgz",
       "integrity": "sha512-RRECPsj7iu/xb5oKYcsFHSppFNnsj/52OVTRKb4zP5onXwVF3zVmmToNcOfGC+CRDpfK/U584fMg38ZHCaElKQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "color-name": "~1.1.4"
@@ -3465,6 +3564,7 @@
       "version": "1.1.4",
       "resolved": "https://registry.npmjs.org/color-name/-/color-name-1.1.4.tgz",
       "integrity": "sha512-dOy+3AuW3a2wNbZHIuMZpTcgjGuLU/uBL/ubcZF9OXbDo8ff4O8yVp5Bf0efS8uEoYo5q4Fx7dY9OgQGXgAsQA==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/combined-stream": {
@@ -3483,6 +3583,7 @@
       "version": "14.0.3",
       "resolved": "https://registry.npmjs.org/commander/-/commander-14.0.3.tgz",
       "integrity": "sha512-H+y0Jo/T1RZ9qPP4Eh1pkcQcLRglraJaSLoyOtHxu6AapkjWVCy2Sit1QQ4x3Dng8qDlSsZEet7g5Pq06MvTgw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=20"
@@ -3499,6 +3600,7 @@
       "version": "9.2.1",
       "resolved": "https://registry.npmjs.org/concurrently/-/concurrently-9.2.1.tgz",
       "integrity": "sha512-fsfrO0MxV64Znoy8/l1vVIjjHa29SZyyqPgQBwhiDcaW8wJc2W3XWVOGx4M3oJBnv/zdUZIIp1gDeS98GzP8Ng==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "chalk": "4.1.2",
@@ -3523,6 +3625,7 @@
       "version": "1.1.0",
       "resolved": "https://registry.npmjs.org/content-disposition/-/content-disposition-1.1.0.tgz",
       "integrity": "sha512-5jRCH9Z/+DRP7rkvY83B+yGIGX96OYdJmzngqnw2SBSxqCFPd0w2km3s5iawpGX8krnwSGmF0FW5Nhr0Hfai3g==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -3536,6 +3639,7 @@
       "version": "1.0.5",
       "resolved": "https://registry.npmjs.org/content-type/-/content-type-1.0.5.tgz",
       "integrity": "sha512-nTjqfcBFEipKdXCv4YDQWCfmcLZKm81ldF0pAopTvyrFGVbcR6P/VAAd5G7N+0tTr8QqiU0tFadD6FK4NtJwOA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.6"
@@ -3545,6 +3649,7 @@
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/convert-source-map/-/convert-source-map-2.0.0.tgz",
       "integrity": "sha512-Kvp459HrV2FEJ1CAsi1Ku+MY3kasH19TFykTz2xWmMeq6bk2NU3XXvfJ+Q61m0xktWwt+1HSYf3JZsTms3aRJg==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/cookie": {
@@ -3564,6 +3669,7 @@
       "version": "1.2.2",
       "resolved": "https://registry.npmjs.org/cookie-signature/-/cookie-signature-1.2.2.tgz",
       "integrity": "sha512-D76uU73ulSXrD1UXF4KE2TMxVVwhsnCgfAyTg9k8P6KGZjlXKrOLe4dJQKI3Bxi5wjesZoFXJWElNWBjPZMbhg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6.6.0"
@@ -3573,6 +3679,7 @@
       "version": "2.8.6",
       "resolved": "https://registry.npmjs.org/cors/-/cors-2.8.6.tgz",
       "integrity": "sha512-tJtZBBHA6vjIAaF6EnIaq6laBBP9aq/Y3ouVJjEfoHbRBcHBAHYcMh/w8LDrk2PvIMMq8gmopa5D4V8RmbrxGw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "object-assign": "^4",
@@ -3590,6 +3697,7 @@
       "version": "9.0.1",
       "resolved": "https://registry.npmjs.org/cosmiconfig/-/cosmiconfig-9.0.1.tgz",
       "integrity": "sha512-hr4ihw+DBqcvrsEDioRO31Z17x71pUYoNe/4h6Z0wB72p7MU7/9gH8Q3s12NFhHPfYBBOV3qyfUxmr/Yn3shnQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "env-paths": "^2.2.1",
@@ -3616,6 +3724,7 @@
       "version": "7.0.6",
       "resolved": "https://registry.npmjs.org/cross-spawn/-/cross-spawn-7.0.6.tgz",
       "integrity": "sha512-uV2QOWP2nWzsy2aMp8aRibhi9dlzF5Hgh5SHaB9OiTGEyDTiJJyx0uy51QXdyWbtAHNua4XJzUKca3OzKUd3vA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "path-key": "^3.1.0",
@@ -3651,6 +3760,7 @@
       "version": "3.0.0",
       "resolved": "https://registry.npmjs.org/cssesc/-/cssesc-3.0.0.tgz",
       "integrity": "sha512-/Tb/JcjK111nNScGob5MNtsntNM1aCNUDipB/TkwZFhyDrrE47SOx/18wF2bbjgc3ZzCSKW1T5nt5EbFoAz/Vg==",
+      "dev": true,
       "license": "MIT",
       "bin": {
         "cssesc": "bin/cssesc"
@@ -3670,6 +3780,7 @@
       "version": "4.0.1",
       "resolved": "https://registry.npmjs.org/data-uri-to-buffer/-/data-uri-to-buffer-4.0.1.tgz",
       "integrity": "sha512-0R9ikRb668HB7QDxT1vkpuUBtqc53YyAwMwGeUFKRojY/NWKvdZ+9UYtRfGmhqNbRkTSVpMbmyhXipFFv2cb/A==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 12"
@@ -3742,6 +3853,7 @@
       "version": "4.4.3",
       "resolved": "https://registry.npmjs.org/debug/-/debug-4.4.3.tgz",
       "integrity": "sha512-RGwwWnwQvkVfavKVt22FGLw+xYSdzARwm0ru6DhTVA3umU5hZc28V3kO4stgYryrTlLpuvgI9GiijltAjNbcqA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "ms": "^2.1.3"
@@ -3766,6 +3878,7 @@
       "version": "1.7.2",
       "resolved": "https://registry.npmjs.org/dedent/-/dedent-1.7.2.tgz",
       "integrity": "sha512-WzMx3mW98SN+zn3hgemf4OzdmyNhhhKz5Ay0pUfQiMQ3e1g+xmTJWp/pKdwKVXhdSkAEGIIzqeuWrL3mV/AXbA==",
+      "dev": true,
       "license": "MIT",
       "peerDependencies": {
         "babel-plugin-macros": "^3.1.0"
@@ -3787,6 +3900,7 @@
       "version": "4.3.1",
       "resolved": "https://registry.npmjs.org/deepmerge/-/deepmerge-4.3.1.tgz",
       "integrity": "sha512-3sUqbMEc77XqpdNO7FRyRog+eW3ph+GYCbj+rK+uYyRMuwsVy0rMiVtPn+QJlKFvWP/1PYpapqYn0Me2knFn+A==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=0.10.0"
@@ -3796,6 +3910,7 @@
       "version": "5.5.0",
       "resolved": "https://registry.npmjs.org/default-browser/-/default-browser-5.5.0.tgz",
       "integrity": "sha512-H9LMLr5zwIbSxrmvikGuI/5KGhZ8E2zH3stkMgM5LpOWDutGM2JZaj460Udnf1a+946zc7YBgrqEWwbk7zHvGw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "bundle-name": "^4.1.0",
@@ -3812,6 +3927,7 @@
       "version": "5.0.1",
       "resolved": "https://registry.npmjs.org/default-browser-id/-/default-browser-id-5.0.1.tgz",
       "integrity": "sha512-x1VCxdX4t+8wVfd1so/9w+vQ4vx7lKd2Qp5tDRutErwmR85OgmfX7RlLRMWafRMY7hbEiXIbudNrjOAPa/hL8Q==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -3824,6 +3940,7 @@
       "version": "3.0.0",
       "resolved": "https://registry.npmjs.org/define-lazy-prop/-/define-lazy-prop-3.0.0.tgz",
       "integrity": "sha512-N+MeXYoqr3pOgn8xfyRPREN7gHakLYjhsHhWGT3fWAiL4IkAt0iDw14QiiEm2bE30c5XX5q0FtAA3CK5f9/BUg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=12"
@@ -3845,6 +3962,7 @@
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/depd/-/depd-2.0.0.tgz",
       "integrity": "sha512-g7nH6P6dyDioJogAAGprGpCtVImJhpPk/roCzdb3fIh61/s/nPsfR6onyMwkCAR/OlC3yBC0lESvUoQEAssIrw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.8"
@@ -3873,6 +3991,7 @@
       "version": "8.0.4",
       "resolved": "https://registry.npmjs.org/diff/-/diff-8.0.4.tgz",
       "integrity": "sha512-DPi0FmjiSU5EvQV0++GFDOJ9ASQUVFh5kD+OzOnYdi7n3Wpm9hWWGfB/O2blfHcMVTL5WkQXSnRiK9makhrcnw==",
+      "dev": true,
       "license": "BSD-3-Clause",
       "engines": {
         "node": ">=0.3.1"
@@ -3890,6 +4009,7 @@
       "version": "17.4.2",
       "resolved": "https://registry.npmjs.org/dotenv/-/dotenv-17.4.2.tgz",
       "integrity": "sha512-nI4U3TottKAcAD9LLud4Cb7b2QztQMUEfHbvhTH09bqXTxnSie8WnjPALV/WMCrJZ6UV/qHJ6L03OqO3LcdYZw==",
+      "dev": true,
       "license": "BSD-2-Clause",
       "engines": {
         "node": ">=12"
@@ -3916,6 +4036,7 @@
       "version": "0.4.18",
       "resolved": "https://registry.npmjs.org/eciesjs/-/eciesjs-0.4.18.tgz",
       "integrity": "sha512-wG99Zcfcys9fZux7Cft8BAX/YrOJLJSZ3jyYPfhZHqN2E+Ffx+QXBDsv3gubEgPtV6dTzJMSQUwk1H98/t/0wQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@ecies/ciphers": "^0.2.5",
@@ -3933,24 +4054,28 @@
       "version": "1.1.1",
       "resolved": "https://registry.npmjs.org/ee-first/-/ee-first-1.1.1.tgz",
       "integrity": "sha512-WMwm9LhRUo+WUaRN+vRuETqG89IgZphVSNkdFgeb6sS/E4OrDIN7t48CAewSHXc6C8lefD8KKfr5vY61brQlow==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/electron-to-chromium": {
       "version": "1.5.336",
       "resolved": "https://registry.npmjs.org/electron-to-chromium/-/electron-to-chromium-1.5.336.tgz",
       "integrity": "sha512-AbH9q9J455r/nLmdNZes0G0ZKcRX73FicwowalLs6ijwOmCJSRRrLX63lcAlzy9ux3dWK1w1+1nsBJEWN11hcQ==",
+      "dev": true,
       "license": "ISC"
     },
     "node_modules/emoji-regex": {
       "version": "8.0.0",
       "resolved": "https://registry.npmjs.org/emoji-regex/-/emoji-regex-8.0.0.tgz",
       "integrity": "sha512-MSjYzcWNOA0ewAHpz0MxpYFvwg6yjy1NG3xteoqz644VCo/RPgnr1/GGt+ic3iJTzQ8Eu3TdM14SawnVUmGE6A==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/encodeurl": {
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/encodeurl/-/encodeurl-2.0.0.tgz",
       "integrity": "sha512-Q0n9HRi4m6JuGIV1eFlmvJB7ZEVxu93IrMyiMsGC0lrMJMWzRgx6WGquyfQgZVb31vhGgXnfmPNNXmxnOkRBrg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.8"
@@ -3986,6 +4111,7 @@
       "version": "2.2.1",
       "resolved": "https://registry.npmjs.org/env-paths/-/env-paths-2.2.1.tgz",
       "integrity": "sha512-+h1lkLKhZMTYjog1VEpJNG7NZJWcuc2DDk/qsqSTRRCOXiLjeQ1d1/udrUGhqMxUgAlwKNZ0cf2uqan5GLuS2A==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6"
@@ -3995,6 +4121,7 @@
       "version": "1.3.4",
       "resolved": "https://registry.npmjs.org/error-ex/-/error-ex-1.3.4.tgz",
       "integrity": "sha512-sqQamAnR14VgCr1A618A3sGrygcpK+HEbenA/HiEAkkUwcZIIB/tgWqHFxWgOyDh4nB4JCRimh79dR5Ywc9MDQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "is-arrayish": "^0.2.1"
@@ -4056,6 +4183,7 @@
       "version": "3.2.0",
       "resolved": "https://registry.npmjs.org/escalade/-/escalade-3.2.0.tgz",
       "integrity": "sha512-WUj2qlxaQtO4g6Pq5c29GTcWGDyd8itL8zTlipgECz3JesAiiOKotd8JU6otB3PACgG6xkJUyVhboMS+bje/jA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6"
@@ -4065,6 +4193,7 @@
       "version": "1.0.3",
       "resolved": "https://registry.npmjs.org/escape-html/-/escape-html-1.0.3.tgz",
       "integrity": "sha512-NiSupZ4OeuGwr68lGIeym/ksIZMJodUGOSCZ/FSnTxcrekbvqrgdUxlJOMpijaKZVjAJrWrGs/6Jy8OMuyj9ow==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/escape-string-regexp": {
@@ -4222,6 +4351,7 @@
       "version": "4.0.1",
       "resolved": "https://registry.npmjs.org/esprima/-/esprima-4.0.1.tgz",
       "integrity": "sha512-eGuFFw7Upda+g4p+QHvnW0RyTX/SVeJBDM/gCtMARO0cLuT2HcEKnTPvhjV6aGeqrCB/sbNop0Kszm0jsaWU4A==",
+      "dev": true,
       "license": "BSD-2-Clause",
       "bin": {
         "esparse": "bin/esparse.js",
@@ -4291,6 +4421,7 @@
       "version": "1.8.1",
       "resolved": "https://registry.npmjs.org/etag/-/etag-1.8.1.tgz",
       "integrity": "sha512-aIL5Fx7mawVa300al2BnEE4iNvo1qETxLrPI/o05L7z6go7fCw1J6EQmbK4FmJ2AS7kgVF/KEZWufBfdClMcPg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.6"
@@ -4318,6 +4449,7 @@
       "version": "3.0.6",
       "resolved": "https://registry.npmjs.org/eventsource-parser/-/eventsource-parser-3.0.6.tgz",
       "integrity": "sha512-Vo1ab+QXPzZ4tCa8SwIHJFaSzy4R6SHf7BY79rFBDf0idraZWAkYrDjDj8uWaSm3S2TK+hJ7/t1CEmZ7jXw+pg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18.0.0"
@@ -4327,6 +4459,7 @@
       "version": "9.6.1",
       "resolved": "https://registry.npmjs.org/execa/-/execa-9.6.1.tgz",
       "integrity": "sha512-9Be3ZoN4LmYR90tUoVu2te2BsbzHfhJyfEiAVfz7N5/zv+jduIfLrV2xdQXOHbaD6KgpGdO9PRPM1Y4Q9QkPkA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@sindresorhus/merge-streams": "^4.0.0",
@@ -4363,6 +4496,7 @@
       "version": "5.2.1",
       "resolved": "https://registry.npmjs.org/express/-/express-5.2.1.tgz",
       "integrity": "sha512-hIS4idWWai69NezIdRt2xFVofaF4j+6INOpJlVOLDO8zXGpUVEVzIYk12UUi2JzjEzWL3IOAxcTubgz9Po0yXw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "accepts": "^2.0.0",
@@ -4406,6 +4540,7 @@
       "version": "8.3.2",
       "resolved": "https://registry.npmjs.org/express-rate-limit/-/express-rate-limit-8.3.2.tgz",
       "integrity": "sha512-77VmFeJkO0/rvimEDuUC5H30oqUC4EyOhyGccfqoLebB0oiEYfM7nwPrsDsBL1gsTpwfzX8SFy2MT3TDyRq+bg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "ip-address": "10.1.0"
@@ -4424,6 +4559,7 @@
       "version": "0.7.2",
       "resolved": "https://registry.npmjs.org/cookie/-/cookie-0.7.2.tgz",
       "integrity": "sha512-yki5XnKuf750l50uGTllt6kKILY4nQ1eNIQatoXEByZ5dWgnKqbnqmTrBE5B4N7lrMJKQ2ytWMiTO2o0v6Ew/w==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.6"
@@ -4433,6 +4569,7 @@
       "version": "1.54.0",
       "resolved": "https://registry.npmjs.org/mime-db/-/mime-db-1.54.0.tgz",
       "integrity": "sha512-aU5EJuIN2WDemCcAp2vFBfp/m4EAhWJnUNSSw0ixs7/kXbd6Pg64EmwJkNdFhB8aWt1sH2CTXrLxo/iAGV3oPQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.6"
@@ -4442,6 +4579,7 @@
       "version": "3.0.2",
       "resolved": "https://registry.npmjs.org/mime-types/-/mime-types-3.0.2.tgz",
       "integrity": "sha512-Lbgzdk0h4juoQ9fCKXW4by0UJqj+nOOrI9MJ1sSj4nI8aI2eo1qmvQEie4VD1glsS250n15LsWsYtCugiStS5A==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "mime-db": "^1.54.0"
@@ -4458,12 +4596,14 @@
       "version": "3.1.3",
       "resolved": "https://registry.npmjs.org/fast-deep-equal/-/fast-deep-equal-3.1.3.tgz",
       "integrity": "sha512-f3qQ9oQy9j2AhBe/H9VC91wLmKBCCU/gDOnKNAYG5hswO7BLKj09Hc5HYNz9cGI++xlpDCIgDaitVs03ATR84Q==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/fast-glob": {
       "version": "3.3.3",
       "resolved": "https://registry.npmjs.org/fast-glob/-/fast-glob-3.3.3.tgz",
       "integrity": "sha512-7MptL8U0cqcFdzIzwOTHoilX9x5BrNqye7Z/LuC7kCMRio1EMSyqRK3BEAUD7sXRq4iT4AzTVuZdhgQ2TCvYLg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@nodelib/fs.stat": "^2.0.2",
@@ -4480,6 +4620,7 @@
       "version": "5.1.2",
       "resolved": "https://registry.npmjs.org/glob-parent/-/glob-parent-5.1.2.tgz",
       "integrity": "sha512-AOIgSQCepiJYwP3ARnGx+5VnTu2HBYdzbGP45eLw1vr3zB3vZLeyed1sC9hnbcOc9/SrMyM5RPQrkGz4aS9Zow==",
+      "dev": true,
       "license": "ISC",
       "dependencies": {
         "is-glob": "^4.0.1"
@@ -4506,6 +4647,7 @@
       "version": "3.1.0",
       "resolved": "https://registry.npmjs.org/fast-uri/-/fast-uri-3.1.0.tgz",
       "integrity": "sha512-iPeeDKJSWf4IEOasVVrknXpaBV0IApz/gp7S2bb7Z4Lljbl2MGJRqInZiUrQwV16cpzw/D3S5j5Julj/gT52AA==",
+      "dev": true,
       "funding": [
         {
           "type": "github",
@@ -4522,6 +4664,7 @@
       "version": "1.20.1",
       "resolved": "https://registry.npmjs.org/fastq/-/fastq-1.20.1.tgz",
       "integrity": "sha512-GGToxJ/w1x32s/D2EKND7kTil4n8OVk/9mycTc4VDza13lOvpUZTGX3mFSCtV9ksdGBVzvsyAVLM6mHFThxXxw==",
+      "dev": true,
       "license": "ISC",
       "dependencies": {
         "reusify": "^1.0.4"
@@ -4548,6 +4691,7 @@
       "version": "3.2.0",
       "resolved": "https://registry.npmjs.org/fetch-blob/-/fetch-blob-3.2.0.tgz",
       "integrity": "sha512-7yAQpD2UMJzLi1Dqv7qFYnPbaPx7ZfFK6PiIxQ4PfkGPyNyl2Ugx+a/umUonmKqjhM4DnfbMvdX6otXq83soQQ==",
+      "dev": true,
       "funding": [
         {
           "type": "github",
@@ -4581,6 +4725,7 @@
       "version": "6.1.0",
       "resolved": "https://registry.npmjs.org/figures/-/figures-6.1.0.tgz",
       "integrity": "sha512-d+l3qxjSesT4V7v2fh+QnmFnUWv9lSpjarhShNTgBOfA0ttejbQUAlHLitbjkoRiDulW0OPoQPYIGhIC8ohejg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "is-unicode-supported": "^2.0.0"
@@ -4609,6 +4754,7 @@
       "version": "7.1.1",
       "resolved": "https://registry.npmjs.org/fill-range/-/fill-range-7.1.1.tgz",
       "integrity": "sha512-YsGpe3WHLK8ZYi4tWDg2Jy3ebRz2rXowDxnld4bkQB00cc/1Zw9AWnC0i9ztDJitivtQvaI9KaLyKrc+hBW0yg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "to-regex-range": "^5.0.1"
@@ -4621,6 +4767,7 @@
       "version": "2.1.1",
       "resolved": "https://registry.npmjs.org/finalhandler/-/finalhandler-2.1.1.tgz",
       "integrity": "sha512-S8KoZgRZN+a5rNwqTxlZZePjT/4cnm0ROV70LedRHZ0p8u9fRID0hJUZQpkKLzro8LfmC8sx23bY6tVNxv8pQA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "debug": "^4.4.0",
@@ -4716,6 +4863,7 @@
       "version": "4.0.10",
       "resolved": "https://registry.npmjs.org/formdata-polyfill/-/formdata-polyfill-4.0.10.tgz",
       "integrity": "sha512-buewHzMvYL29jdeQTVILecSaZKnt/RJWjoZCF5OW60Z67/GmSLBkOFM7qh1PI3zFNtJbaZL5eQu1vLfazOwj4g==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "fetch-blob": "^3.1.2"
@@ -4728,6 +4876,7 @@
       "version": "0.2.0",
       "resolved": "https://registry.npmjs.org/forwarded/-/forwarded-0.2.0.tgz",
       "integrity": "sha512-buRG0fpBtRHSTCOASe6hD258tEubFoRLb4ZNA6NxMVHNw2gOcwHo9wyablzMzOA5z9xA9L1KNjk/Nt6MT9aYow==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.6"
@@ -4737,6 +4886,7 @@
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/fresh/-/fresh-2.0.0.tgz",
       "integrity": "sha512-Rx/WycZ60HOaqLKAi6cHRKKI7zxWbJ31MhntmtwMoaTeF7XFH9hhBp8vITaMidfljRQ6eYWCKkaTK+ykVJHP2A==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.8"
@@ -4746,6 +4896,7 @@
       "version": "11.3.4",
       "resolved": "https://registry.npmjs.org/fs-extra/-/fs-extra-11.3.4.tgz",
       "integrity": "sha512-CTXd6rk/M3/ULNQj8FBqBWHYBVYybQ3VPBw0xGKFe3tuH7ytT6ACnvzpIQ3UZtB8yvUKC2cXn1a+x+5EVQLovA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "graceful-fs": "^4.2.0",
@@ -4760,6 +4911,7 @@
       "version": "2.0.1",
       "resolved": "https://registry.npmjs.org/universalify/-/universalify-2.0.1.tgz",
       "integrity": "sha512-gptHNQghINnc/vTGIk0SOFGFNXw7JVrlRUtConJRlvaw6DuX0wO5Jeko9sWrMBhh+PsYAZ7oXAiOnf/UKogyiw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 10.0.0"
@@ -4792,12 +4944,14 @@
       "version": "3.1.0",
       "resolved": "https://registry.npmjs.org/fuzzysort/-/fuzzysort-3.1.0.tgz",
       "integrity": "sha512-sR9BNCjBg6LNgwvxlBd0sBABvQitkLzoVY9MYYROQVX/FvfJ4Mai9LsGhDgd8qYdds0bY77VzYd5iuB+v5rwQQ==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/gensync": {
       "version": "1.0.0-beta.2",
       "resolved": "https://registry.npmjs.org/gensync/-/gensync-1.0.0-beta.2.tgz",
       "integrity": "sha512-3hN7NaskYvMDLQY55gnW3NQ+mesEAepTqlg+VEbj7zzqEMBVNhzcGYYeqFo/TlYz6eQiFcp1HcsCZO+nGgS8zg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6.9.0"
@@ -4807,6 +4961,7 @@
       "version": "2.0.5",
       "resolved": "https://registry.npmjs.org/get-caller-file/-/get-caller-file-2.0.5.tgz",
       "integrity": "sha512-DyFP3BM/3YHTQOCUL/w0OZHR0lpKeGrxotcHWcqNEdnltqFwXVfhEBQ94eIo34AfQpo0rGki4cyIiftY06h2Fg==",
+      "dev": true,
       "license": "ISC",
       "engines": {
         "node": "6.* || 8.* || >= 10.*"
@@ -4816,6 +4971,7 @@
       "version": "1.5.0",
       "resolved": "https://registry.npmjs.org/get-east-asian-width/-/get-east-asian-width-1.5.0.tgz",
       "integrity": "sha512-CQ+bEO+Tva/qlmw24dCejulK5pMzVnUOFOijVogd3KQs07HnRIgp8TGipvCCRT06xeYEbpbgwaCxglFyiuIcmA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -4852,6 +5008,7 @@
       "version": "1.0.0",
       "resolved": "https://registry.npmjs.org/get-own-enumerable-keys/-/get-own-enumerable-keys-1.0.0.tgz",
       "integrity": "sha512-PKsK2FSrQCyxcGHsGrLDcK0lx+0Ke+6e8KFFozA9/fIQLhQzPaRvJFdcz7+Axg3jUH/Mq+NI4xa5u/UT2tQskA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=14.16"
@@ -4877,6 +5034,7 @@
       "version": "9.0.1",
       "resolved": "https://registry.npmjs.org/get-stream/-/get-stream-9.0.1.tgz",
       "integrity": "sha512-kVCxPF3vQM/N0B1PmoqVUqgHP+EeVjmZSQn+1oCRPxd2P21P2F19lIgbR3HBosbB1PUhOAoctJnfEn2GbN2eZA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@sec-ant/readable-stream": "^0.4.1",
@@ -4937,6 +5095,7 @@
       "version": "16.13.2",
       "resolved": "https://registry.npmjs.org/graphql/-/graphql-16.13.2.tgz",
       "integrity": "sha512-5bJ+nf/UCpAjHM8i06fl7eLyVC9iuNAjm9qzkiu2ZGhM0VscSvS6WDPfAwkdkBuoXGM9FJSbKl6wylMwP9Ktig==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": "^12.22.0 || ^14.16.0 || ^16.0.0 || >=17.0.0"
@@ -4946,6 +5105,7 @@
       "version": "4.0.0",
       "resolved": "https://registry.npmjs.org/has-flag/-/has-flag-4.0.0.tgz",
       "integrity": "sha512-EykJT/Q1KjTWctppgIAgfSO0tKVuZUjhgMr17kqTumMl6Afv3EISleU7qZUzoXDFTAHTDC4NOoG/ZxU3EvlMPQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=8"
@@ -4994,6 +5154,7 @@
       "version": "4.0.3",
       "resolved": "https://registry.npmjs.org/headers-polyfill/-/headers-polyfill-4.0.3.tgz",
       "integrity": "sha512-IScLbePpkvO846sIwOtOTDjutRMWdXdJmXdMvk6gCBHxFO8d+QKOQedyZSxFTTFYRSmlgSTDtXqqq4pcenBXLQ==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/hermes-estree": {
@@ -5017,6 +5178,7 @@
       "version": "4.12.14",
       "resolved": "https://registry.npmjs.org/hono/-/hono-4.12.14.tgz",
       "integrity": "sha512-am5zfg3yu6sqn5yjKBNqhnTX7Cv+m00ox+7jbaKkrLMRJ4rAdldd1xPd/JzbBWspqaQv6RSTrgFN95EsfhC+7w==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=16.9.0"
@@ -5046,6 +5208,7 @@
       "version": "2.0.1",
       "resolved": "https://registry.npmjs.org/http-errors/-/http-errors-2.0.1.tgz",
       "integrity": "sha512-4FbRdAX+bSdmo4AUFuS0WNiPz8NgFt+r8ThgNWmlrjQjt1Q7ZR9+zTlce2859x4KSXrwIsaeTqDoKQmtP8pLmQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "depd": "~2.0.0",
@@ -5066,6 +5229,7 @@
       "version": "7.0.6",
       "resolved": "https://registry.npmjs.org/https-proxy-agent/-/https-proxy-agent-7.0.6.tgz",
       "integrity": "sha512-vK9P5/iUfdl95AI+JVyUuIcVtd4ofvtrOr3HNtM2yxC9bnMbEdp3x01OhQNnjb8IJYi38VlTE3mBXwcfvywuSw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "agent-base": "^7.1.2",
@@ -5079,6 +5243,7 @@
       "version": "8.0.1",
       "resolved": "https://registry.npmjs.org/human-signals/-/human-signals-8.0.1.tgz",
       "integrity": "sha512-eKCa6bwnJhvxj14kZk5NCPc6Hb6BdsU9DZcOnmQKSnO1VKrfV0zCvtttPZUsBvjmNDn8rpcJfpwSYnHBjc95MQ==",
+      "dev": true,
       "license": "Apache-2.0",
       "engines": {
         "node": ">=18.18.0"
@@ -5088,6 +5253,7 @@
       "version": "0.7.2",
       "resolved": "https://registry.npmjs.org/iconv-lite/-/iconv-lite-0.7.2.tgz",
       "integrity": "sha512-im9DjEDQ55s9fL4EYzOAv0yMqmMBSZp6G0VvFyTMPKWxiSBHUj9NW/qqLmXUwXrrM7AvqSlTCfvqRb0cM8yYqw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "safer-buffer": ">= 2.1.2 < 3.0.0"
@@ -5104,6 +5270,7 @@
       "version": "5.3.2",
       "resolved": "https://registry.npmjs.org/ignore/-/ignore-5.3.2.tgz",
       "integrity": "sha512-hsBTNUqQTDwkWtcdYI2i06Y/nUBEsNEDJKjWdigLvegy8kDuJAS8uRlpkkcQpyEXL0Z/pjDy5HBmMjRCJ2gq+g==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 4"
@@ -5113,6 +5280,7 @@
       "version": "3.3.1",
       "resolved": "https://registry.npmjs.org/import-fresh/-/import-fresh-3.3.1.tgz",
       "integrity": "sha512-TR3KfrTZTYLPB6jUjfx6MF9WcWrHL9su5TObK4ZkYgBdWKPOFoSoQIdEuTuR82pmtxH2spWG9h6etwfr1pLBqQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "parent-module": "^1.0.0",
@@ -5149,12 +5317,14 @@
       "version": "2.0.4",
       "resolved": "https://registry.npmjs.org/inherits/-/inherits-2.0.4.tgz",
       "integrity": "sha512-k/vGaX4/Yla3WzyMCvTQOXYeIHvqOKtnqBduzTHpzpQZzAskKMhZ2K+EnBiSM9zGSoIFeMpXKxa4dYeZIQqewQ==",
+      "dev": true,
       "license": "ISC"
     },
     "node_modules/ip-address": {
       "version": "10.1.0",
       "resolved": "https://registry.npmjs.org/ip-address/-/ip-address-10.1.0.tgz",
       "integrity": "sha512-XXADHxXmvT9+CRxhXg56LJovE+bmWnEWB78LB83VZTprKTmaC5QfruXocxzTZ2Kl0DNwKuBdlIhjL8LeY8Sf8Q==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 12"
@@ -5164,6 +5334,7 @@
       "version": "1.9.1",
       "resolved": "https://registry.npmjs.org/ipaddr.js/-/ipaddr.js-1.9.1.tgz",
       "integrity": "sha512-0KI/607xoxSToH7GjN1FfSbLoU0+btTicjsQSWQlh/hZykN8KpmMf7uYwPW3R+akZ6R/w18ZlXSHBYXiYUPO3g==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.10"
@@ -5173,12 +5344,14 @@
       "version": "0.2.1",
       "resolved": "https://registry.npmjs.org/is-arrayish/-/is-arrayish-0.2.1.tgz",
       "integrity": "sha512-zz06S8t0ozoDXMG+ube26zeCTNXcKIPJZJi8hBrF4idCLms4CG9QtK7qBl1boi5ODzFpjswb5JPmHCbMpjaYzg==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/is-docker": {
       "version": "3.0.0",
       "resolved": "https://registry.npmjs.org/is-docker/-/is-docker-3.0.0.tgz",
       "integrity": "sha512-eljcgEDlEns/7AXFosB5K/2nCM4P7FQPkGc/DWLy5rmFEWvZayGrik1d9/QIY5nJ4f9YsVvBkA6kJpHn9rISdQ==",
+      "dev": true,
       "license": "MIT",
       "bin": {
         "is-docker": "cli.js"
@@ -5194,6 +5367,7 @@
       "version": "2.1.1",
       "resolved": "https://registry.npmjs.org/is-extglob/-/is-extglob-2.1.1.tgz",
       "integrity": "sha512-SbKbANkN603Vi4jEZv49LeVJMn4yGwsbzZworEoyEiutsN3nJYdbO36zfhGJ6QEDpOZIFkDtnq5JRxmvl3jsoQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=0.10.0"
@@ -5203,6 +5377,7 @@
       "version": "3.0.0",
       "resolved": "https://registry.npmjs.org/is-fullwidth-code-point/-/is-fullwidth-code-point-3.0.0.tgz",
       "integrity": "sha512-zymm5+u+sCsSWyD9qNaejV3DFvhCKclKdizYaJUuHA83RLjb7nSuGnddCHGv0hk+KY7BMAlsWeK4Ueg6EV6XQg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=8"
@@ -5212,6 +5387,7 @@
       "version": "4.0.3",
       "resolved": "https://registry.npmjs.org/is-glob/-/is-glob-4.0.3.tgz",
       "integrity": "sha512-xelSayHH36ZgE7ZWhli7pW34hNbNl8Ojv5KVmkJD4hBdD3th8Tfk9vYasLM+mXWOZhFkgZfxhLSnrwRr4elSSg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "is-extglob": "^2.1.1"
@@ -5224,6 +5400,7 @@
       "version": "1.0.0",
       "resolved": "https://registry.npmjs.org/is-in-ssh/-/is-in-ssh-1.0.0.tgz",
       "integrity": "sha512-jYa6Q9rH90kR1vKB6NM7qqd1mge3Fx4Dhw5TVlK1MUBqhEOuCagrEHMevNuCcbECmXZ0ThXkRm+Ymr51HwEPAw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=20"
@@ -5236,6 +5413,7 @@
       "version": "1.0.0",
       "resolved": "https://registry.npmjs.org/is-inside-container/-/is-inside-container-1.0.0.tgz",
       "integrity": "sha512-KIYLCCJghfHZxqjYBE7rEy0OBuTd5xCHS7tHVgvCLkx7StIoaxwNW3hCALgEUjFfeRk+MG/Qxmp/vtETEF3tRA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "is-docker": "^3.0.0"
@@ -5254,6 +5432,7 @@
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/is-interactive/-/is-interactive-2.0.0.tgz",
       "integrity": "sha512-qP1vozQRI+BMOPcjFzrjXuQvdak2pHNUMZoeG2eRbiSqyvbEf/wQtEOTOX1guk6E3t36RkaqiSt8A/6YElNxLQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=12"
@@ -5266,12 +5445,14 @@
       "version": "1.2.0",
       "resolved": "https://registry.npmjs.org/is-node-process/-/is-node-process-1.2.0.tgz",
       "integrity": "sha512-Vg4o6/fqPxIjtxgUH5QLJhwZ7gW5diGCVlXpuUfELC62CuxM1iHcRe51f2W1FDy04Ai4KJkagKjx3XaqyfRKXw==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/is-number": {
       "version": "7.0.0",
       "resolved": "https://registry.npmjs.org/is-number/-/is-number-7.0.0.tgz",
       "integrity": "sha512-41Cifkg6e8TylSpdtTpeLVMqvSBEVzTttHvERD741+pnZ8ANv0004MRL43QKPDlK9cGvNp6NZWZUBlbGXYxxng==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=0.12.0"
@@ -5281,6 +5462,7 @@
       "version": "3.0.0",
       "resolved": "https://registry.npmjs.org/is-obj/-/is-obj-3.0.0.tgz",
       "integrity": "sha512-IlsXEHOjtKhpN8r/tRFj2nDyTmHvcfNeu/nrRIcXE17ROeatXchkojffa1SpdqW4cr/Fj6QkEf/Gn4zf6KKvEQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=12"
@@ -5293,6 +5475,7 @@
       "version": "4.1.0",
       "resolved": "https://registry.npmjs.org/is-plain-obj/-/is-plain-obj-4.1.0.tgz",
       "integrity": "sha512-+Pgi+vMuUNkJyExiMBt5IlFoMyKnr5zhJ4Uspz58WOhBF5QoIZkFyNHIbBAtHwzVAgk5RtndVNsDRN61/mmDqg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=12"
@@ -5312,12 +5495,14 @@
       "version": "4.0.0",
       "resolved": "https://registry.npmjs.org/is-promise/-/is-promise-4.0.0.tgz",
       "integrity": "sha512-hvpoI6korhJMnej285dSg6nu1+e6uxs7zG3BYAm5byqDsgJNWwxzM6z6iZiAgQR4TJ30JmBTOwqZUw3WlyH3AQ==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/is-regexp": {
       "version": "3.1.0",
       "resolved": "https://registry.npmjs.org/is-regexp/-/is-regexp-3.1.0.tgz",
       "integrity": "sha512-rbku49cWloU5bSMI+zaRaXdQHXnthP6DZ/vLnfdSKyL4zUzuWnomtOEiZZOd+ioQ+avFo/qau3KPTc7Fjy1uPA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=12"
@@ -5330,6 +5515,7 @@
       "version": "4.0.1",
       "resolved": "https://registry.npmjs.org/is-stream/-/is-stream-4.0.1.tgz",
       "integrity": "sha512-Dnz92NInDqYckGEUJv689RbRiTSEHCQ7wOVeALbkOz999YpqT46yMRIGtSNl2iCL1waAZSx40+h59NV/EwzV/A==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -5342,6 +5528,7 @@
       "version": "2.1.0",
       "resolved": "https://registry.npmjs.org/is-unicode-supported/-/is-unicode-supported-2.1.0.tgz",
       "integrity": "sha512-mE00Gnza5EEB3Ds0HfMyllZzbBrmLOX3vfWoj9A9PEnTfratQ/BcaJOuMhnkhjXvb2+FkY3VuHqtAGpTPmglFQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -5354,6 +5541,7 @@
       "version": "3.1.1",
       "resolved": "https://registry.npmjs.org/is-wsl/-/is-wsl-3.1.1.tgz",
       "integrity": "sha512-e6rvdUCiQCAuumZslxRJWR/Doq4VpPR82kqclvcS0efgt430SlGIk05vdCN58+VrzgtIcfNODjozVielycD4Sw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "is-inside-container": "^1.0.0"
@@ -5369,6 +5557,7 @@
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/isexe/-/isexe-2.0.0.tgz",
       "integrity": "sha512-RHxMLp9lnKHGHRng9QFhRCMbYAcVpn69smSGcq3f36xjgVVWThj4qqLbTLlq7Ssj8B+fIQ1EuCEGI2lKsyQeIw==",
+      "dev": true,
       "license": "ISC"
     },
     "node_modules/istanbul-lib-coverage": {
@@ -5436,6 +5625,7 @@
       "version": "6.2.2",
       "resolved": "https://registry.npmjs.org/jose/-/jose-6.2.2.tgz",
       "integrity": "sha512-d7kPDd34KO/YnzaDOlikGpOurfF0ByC2sEV4cANCtdqLlTfBlw2p14O/5d/zv40gJPbIQxfES3nSx1/oYNyuZQ==",
+      "dev": true,
       "license": "MIT",
       "funding": {
         "url": "https://github.com/sponsors/panva"
@@ -5445,12 +5635,14 @@
       "version": "4.0.0",
       "resolved": "https://registry.npmjs.org/js-tokens/-/js-tokens-4.0.0.tgz",
       "integrity": "sha512-RdJUflcE3cUzKiMqQgsCu06FPu9UdIJO0beYbPhHN4k6apgJtifcoCtT9bcxOpYBtpD2kCM6Sbzg4CausW/PKQ==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/js-yaml": {
       "version": "4.1.1",
       "resolved": "https://registry.npmjs.org/js-yaml/-/js-yaml-4.1.1.tgz",
       "integrity": "sha512-qQKT4zQxXl8lLwBtHMWwaTcGfFOZviOJet3Oy/xmGk2gZH677CJM9EvtfdSkgWcATZhj/55JZ0rmy3myCT5lsA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "argparse": "^2.0.1"
@@ -5565,6 +5757,7 @@
       "version": "3.1.0",
       "resolved": "https://registry.npmjs.org/jsesc/-/jsesc-3.1.0.tgz",
       "integrity": "sha512-/sM3dO2FOzXjKQhJuo0Q173wf2KOo8t4I8vHy6lF9poUp7bKT0/NHE8fPX23PwfhnykfqnC2xRxOnVw5XuGIaA==",
+      "dev": true,
       "license": "MIT",
       "bin": {
         "jsesc": "bin/jsesc"
@@ -5584,6 +5777,7 @@
       "version": "2.3.1",
       "resolved": "https://registry.npmjs.org/json-parse-even-better-errors/-/json-parse-even-better-errors-2.3.1.tgz",
       "integrity": "sha512-xyFwyhro/JEof6Ghe2iz2NcXoj2sloNsWr/XsERDK/oiPCfaNhl5ONfp+jQdAZRQQ0IJWNzH9zIZF7li91kh2w==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/json-schema-traverse": {
@@ -5597,6 +5791,7 @@
       "version": "8.0.2",
       "resolved": "https://registry.npmjs.org/json-schema-typed/-/json-schema-typed-8.0.2.tgz",
       "integrity": "sha512-fQhoXdcvc3V28x7C7BMs4P5+kNlgUURe2jmUT1T//oBRMDrqy1QPelJimwZGo7Hg9VPV3EQV5Bnq4hbFy2vetA==",
+      "dev": true,
       "license": "BSD-2-Clause"
     },
     "node_modules/json-stable-stringify-without-jsonify": {
@@ -5610,6 +5805,7 @@
       "version": "2.2.3",
       "resolved": "https://registry.npmjs.org/json5/-/json5-2.2.3.tgz",
       "integrity": "sha512-XmOWe7eyHYH14cLdVPoyg+GOH3rYX++KpzrylJwSW98t3Nk+U8XOl8FWKOgwtzdb8lXGf6zYwDUzeHMWfxasyg==",
+      "dev": true,
       "license": "MIT",
       "bin": {
         "json5": "lib/cli.js"
@@ -5622,6 +5818,7 @@
       "version": "6.2.0",
       "resolved": "https://registry.npmjs.org/jsonfile/-/jsonfile-6.2.0.tgz",
       "integrity": "sha512-FGuPw30AdOIUTRMC2OMRtQV+jkVj2cfPqSeWXv1NEAJ1qZ5zb1X6z1mFhbfOB/iy3ssJCD+3KuZ8r8C3uVFlAg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "universalify": "^2.0.0"
@@ -5634,6 +5831,7 @@
       "version": "2.0.1",
       "resolved": "https://registry.npmjs.org/universalify/-/universalify-2.0.1.tgz",
       "integrity": "sha512-gptHNQghINnc/vTGIk0SOFGFNXw7JVrlRUtConJRlvaw6DuX0wO5Jeko9sWrMBhh+PsYAZ7oXAiOnf/UKogyiw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 10.0.0"
@@ -5653,6 +5851,7 @@
       "version": "4.1.5",
       "resolved": "https://registry.npmjs.org/kleur/-/kleur-4.1.5.tgz",
       "integrity": "sha512-o+NO+8WrRiQEE4/7nwRJhN1HWpVmJm511pBHUxPLtp0BUISzlBplORYSmTclCnJvQq2tKu/sgl3xVpkc7ZWuQQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6"
@@ -5925,6 +6124,7 @@
       "version": "1.2.4",
       "resolved": "https://registry.npmjs.org/lines-and-columns/-/lines-and-columns-1.2.4.tgz",
       "integrity": "sha512-7ylylesZQ/PV29jhEDl3Ufjo6ZX7gCqJr5F7PKrqc93v7fzSymt1BpwEU8nAUXs8qzzvqhbjhK5QZg6Mt/HkBg==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/locate-path": {
@@ -5954,6 +6154,7 @@
       "version": "6.0.0",
       "resolved": "https://registry.npmjs.org/log-symbols/-/log-symbols-6.0.0.tgz",
       "integrity": "sha512-i24m8rpwhmPIS4zscNzK6MSEhk0DUWa/8iYQWxhffV8jkI4Phvs3F+quL5xvS0gdQR0FyTCMMH33Y78dDTzzIw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "chalk": "^5.3.0",
@@ -5970,6 +6171,7 @@
       "version": "5.6.2",
       "resolved": "https://registry.npmjs.org/chalk/-/chalk-5.6.2.tgz",
       "integrity": "sha512-7NzBL0rN6fMUW+f7A6Io4h40qQlG+xGmtMxfbnH/K7TAtt8JQWVQK+6g0UXKMeVJoyV5EkkNsErQ8pVD3bLHbA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": "^12.17.0 || ^14.13 || >=16.0.0"
@@ -5982,6 +6184,7 @@
       "version": "1.3.0",
       "resolved": "https://registry.npmjs.org/is-unicode-supported/-/is-unicode-supported-1.3.0.tgz",
       "integrity": "sha512-43r2mRvz+8JRIKnWJ+3j8JtjRKZ6GmjzfaE/qiBJnikNnYv/6bagRJ1kUhNk8R5EX/GkobD+r+sfxCPJsiKBLQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=12"
@@ -5994,6 +6197,7 @@
       "version": "5.1.1",
       "resolved": "https://registry.npmjs.org/lru-cache/-/lru-cache-5.1.1.tgz",
       "integrity": "sha512-KpNARQA3Iwv+jTA0utUVVbrh+Jlrr1Fv0e56GGzAFOXN7dk/FviaDW8LHmK52DlcH4WP2n6gI8vN1aesBFgo9w==",
+      "dev": true,
       "license": "ISC",
       "dependencies": {
         "yallist": "^3.0.2"
@@ -6089,6 +6293,7 @@
       "version": "1.1.0",
       "resolved": "https://registry.npmjs.org/media-typer/-/media-typer-1.1.0.tgz",
       "integrity": "sha512-aisnrDP4GNe06UcKFnV5bfMNPBUw4jsLGaWwWfnH3v02GnBuXX2MCVn5RbrWo0j3pczUilYblq7fQ7Nw2t5XKw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.8"
@@ -6098,6 +6303,7 @@
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/merge-descriptors/-/merge-descriptors-2.0.0.tgz",
       "integrity": "sha512-Snk314V5ayFLhp3fkUREub6WtjBfPdCPY1Ln8/8munuLuiYhsABgBVWsozAG+MWMbVEvcdcpbi9R7ww22l9Q3g==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -6110,12 +6316,14 @@
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/merge-stream/-/merge-stream-2.0.0.tgz",
       "integrity": "sha512-abv/qOcuPfk3URPfDzmZU1LKmuw8kT+0nIHvKrKgFrwifol/doWcdA4ZqsWQ8ENrFKkd67Mfpo/LovbIUsbt3w==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/merge2": {
       "version": "1.4.1",
       "resolved": "https://registry.npmjs.org/merge2/-/merge2-1.4.1.tgz",
       "integrity": "sha512-8q7VEgMJW4J8tcfVPy8g09NcQwZdbwFEqhe/WZkoIzjn/3TGDwtOCYtXGxA3O8tPzpczCCDgv+P2P5y00ZJOOg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 8"
@@ -6125,6 +6333,7 @@
       "version": "4.0.8",
       "resolved": "https://registry.npmjs.org/micromatch/-/micromatch-4.0.8.tgz",
       "integrity": "sha512-PXwfBhYu0hBCPw8Dn0E+WDYb7af3dSLVWKi3HGv84IdF4TyFoC0ysxFd0Goxw7nSv4T/PzEJQxsYsEiFCKo2BA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "braces": "^3.0.3",
@@ -6138,6 +6347,7 @@
       "version": "2.3.2",
       "resolved": "https://registry.npmjs.org/picomatch/-/picomatch-2.3.2.tgz",
       "integrity": "sha512-V7+vQEJ06Z+c5tSye8S+nHUfI51xoXIXjHQ99cQtKUkQqqO1kO/KCJUfZXuB47h/YBlDhah2H3hdUGXn8ie0oA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=8.6"
@@ -6171,6 +6381,7 @@
       "version": "2.1.0",
       "resolved": "https://registry.npmjs.org/mimic-fn/-/mimic-fn-2.1.0.tgz",
       "integrity": "sha512-OqbOk5oEQeAZ8WXWydlu9HJjz9WVdEIvamMCcXmuqUYjTknH/sqsWvhQ3vgwKFRR1HpjvNBKQ37nbJgYzGqGcg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6"
@@ -6180,6 +6391,7 @@
       "version": "5.0.1",
       "resolved": "https://registry.npmjs.org/mimic-function/-/mimic-function-5.0.1.tgz",
       "integrity": "sha512-VP79XUPxV2CigYP3jWwAUFSku2aKqBH7uTAapFWCBqutsbmDo96KY5o8uh6U+/YSIn5OxJnXp73beVkpqMIGhA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -6215,6 +6427,7 @@
       "version": "1.2.8",
       "resolved": "https://registry.npmjs.org/minimist/-/minimist-1.2.8.tgz",
       "integrity": "sha512-2yyAR8qBkN3YuheJanUpWC5U3bb5osDywNB8RzDVlDwDHbocAJveqqj1u8+SVD7jkWT4yvsHCpWqqWqAxb0zCA==",
+      "dev": true,
       "license": "MIT",
       "funding": {
         "url": "https://github.com/sponsors/ljharb"
@@ -6224,12 +6437,14 @@
       "version": "2.1.3",
       "resolved": "https://registry.npmjs.org/ms/-/ms-2.1.3.tgz",
       "integrity": "sha512-6FlzubTLZG3J2a/NVCAleEhjzq5oxgHyaCU9yYXvcLsvoVaHJq/s5xXI6/XXP6tz7R9xAOtHnSO/tXtF3WRTlA==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/msw": {
       "version": "2.13.3",
       "resolved": "https://registry.npmjs.org/msw/-/msw-2.13.3.tgz",
       "integrity": "sha512-/F49bxavkNGfreMlrKmTxZs6YorjfMbbDLd89Q3pWi+cXGtQQNXXaHt4MkXN7li91xnQJ24HWXqW9QDm5id33w==",
+      "dev": true,
       "hasInstallScript": true,
       "license": "MIT",
       "dependencies": {
@@ -6274,6 +6489,7 @@
       "version": "6.0.1",
       "resolved": "https://registry.npmjs.org/tough-cookie/-/tough-cookie-6.0.1.tgz",
       "integrity": "sha512-LktZQb3IeoUWB9lqR5EWTHgW/VTITCXg4D21M+lvybRVdylLrRMnqaIONLVb5mav8vM19m44HIcGq4qASeu2Qw==",
+      "dev": true,
       "license": "BSD-3-Clause",
       "dependencies": {
         "tldts": "^7.0.5"
@@ -6286,6 +6502,7 @@
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/mute-stream/-/mute-stream-2.0.0.tgz",
       "integrity": "sha512-WWdIxpyjEn+FhQJQQv9aQAYlHoNVdzIzUySNV1gHUPDSdZJ3yZn7pAAbQcV7B56Mvu881q9FZV+0Vx2xC44VWA==",
+      "dev": true,
       "license": "ISC",
       "engines": {
         "node": "^18.17.0 || >=20.5.0"
@@ -6320,6 +6537,7 @@
       "version": "1.0.0",
       "resolved": "https://registry.npmjs.org/negotiator/-/negotiator-1.0.0.tgz",
       "integrity": "sha512-8Ofs/AUQh8MaEcrlq5xOX0CQ9ypTF5dl78mjlMNfOK08fzpgTHQRQPBxcPlEtIw0yRpws+Zo/3r+5WRby7u3Gg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.6"
@@ -6330,6 +6548,7 @@
       "resolved": "https://registry.npmjs.org/node-domexception/-/node-domexception-1.0.0.tgz",
       "integrity": "sha512-/jKZoMpw0F8GRwl4/eLROPA3cfcXtLApP0QzLmUT/HuPCZWyB7IY9ZrMeKw2O/nFIqPQB3PVM9aYm0F312AXDQ==",
       "deprecated": "Use your platform's native DOMException instead",
+      "dev": true,
       "funding": [
         {
           "type": "github",
@@ -6369,12 +6588,14 @@
       "version": "2.0.37",
       "resolved": "https://registry.npmjs.org/node-releases/-/node-releases-2.0.37.tgz",
       "integrity": "sha512-1h5gKZCF+pO/o3Iqt5Jp7wc9rH3eJJ0+nh/CIoiRwjRxde/hAHyLPXYN4V3CqKAbiZPSeJFSWHmJsbkicta0Eg==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/npm-run-path": {
       "version": "6.0.0",
       "resolved": "https://registry.npmjs.org/npm-run-path/-/npm-run-path-6.0.0.tgz",
       "integrity": "sha512-9qny7Z9DsQU8Ou39ERsPU4OZQlSTP47ShQzuKZ6PRXpYLtIFgl/DEBYEXKlvcEa+9tHVcK8CF81Y2V72qaZhWA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "path-key": "^4.0.0",
@@ -6391,6 +6612,7 @@
       "version": "4.0.0",
       "resolved": "https://registry.npmjs.org/path-key/-/path-key-4.0.0.tgz",
       "integrity": "sha512-haREypq7xkM7ErfgIyA0z+Bj4AGKlMSdlQE2jvJo6huWD1EdkKYV+G/T4nq0YEF2vgTT8kqMFKo1uHn950r4SQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=12"
@@ -6403,6 +6625,7 @@
       "version": "4.1.1",
       "resolved": "https://registry.npmjs.org/object-assign/-/object-assign-4.1.1.tgz",
       "integrity": "sha512-rJgTQnkUnH1sFw8yT6VSU3zD3sWmu6sZhIseY8VX+GRu3P6F7Fu+JNDoXfklElbLJSnc3FUQHVe4cU5hj+BcUg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=0.10.0"
@@ -6412,6 +6635,7 @@
       "version": "1.13.4",
       "resolved": "https://registry.npmjs.org/object-inspect/-/object-inspect-1.13.4.tgz",
       "integrity": "sha512-W67iLl4J2EXEGTbfeHCffrjDfitvLANg0UlX3wFUUSTx92KXRFegMHUVgSqE+wvhAbi4WqjGg9czysTV2Epbew==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.4"
@@ -6424,6 +6648,7 @@
       "version": "1.1.33",
       "resolved": "https://registry.npmjs.org/object-treeify/-/object-treeify-1.1.33.tgz",
       "integrity": "sha512-EFVjAYfzWqWsBMRHPMAXLCDIJnpMhdWAqR7xG6M6a2cs6PMFpl/+Z20w9zDW4vkxOFfddegBKq9Rehd0bxWE7A==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 10"
@@ -6444,6 +6669,7 @@
       "version": "2.4.1",
       "resolved": "https://registry.npmjs.org/on-finished/-/on-finished-2.4.1.tgz",
       "integrity": "sha512-oVlzkg3ENAhCk2zdv7IJwd/QUD4z2RxRwpkcGY8psCVcCYZNq4wYnVWALHM+brtuJjePWiYF/ClmuDr8Ch5+kg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "ee-first": "1.1.1"
@@ -6456,6 +6682,7 @@
       "version": "1.4.0",
       "resolved": "https://registry.npmjs.org/once/-/once-1.4.0.tgz",
       "integrity": "sha512-lNaJgI+2Q5URQBkccEKHTQOPaXdUxnZZElQTZY0MFUAuaEqe1E+Nyvgdz/aIyNi6Z9MzO5dv1H8n58/GELp3+w==",
+      "dev": true,
       "license": "ISC",
       "dependencies": {
         "wrappy": "1"
@@ -6465,6 +6692,7 @@
       "version": "7.0.0",
       "resolved": "https://registry.npmjs.org/onetime/-/onetime-7.0.0.tgz",
       "integrity": "sha512-VXJjc87FScF88uafS3JllDgvAm+c/Slfz06lorj2uAY34rlUu0Nt+v8wreiImcrgAjjIHp1rXpTDlLOGw29WwQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "mimic-function": "^5.0.0"
@@ -6480,6 +6708,7 @@
       "version": "11.0.0",
       "resolved": "https://registry.npmjs.org/open/-/open-11.0.0.tgz",
       "integrity": "sha512-smsWv2LzFjP03xmvFoJ331ss6h+jixfA4UUV/Bsiyuu4YJPfN+FIQGOIiv4w9/+MoHkfkJ22UIaQWRVFRfH6Vw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "default-browser": "^5.4.0",
@@ -6518,6 +6747,7 @@
       "version": "8.2.0",
       "resolved": "https://registry.npmjs.org/ora/-/ora-8.2.0.tgz",
       "integrity": "sha512-weP+BZ8MVNnlCm8c0Qdc1WSWq4Qn7I+9CJGm7Qali6g44e/PUzbjNqJX5NJ9ljlNMosfJvg1fKEGILklK9cwnw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "chalk": "^5.3.0",
@@ -6541,6 +6771,7 @@
       "version": "6.2.2",
       "resolved": "https://registry.npmjs.org/ansi-regex/-/ansi-regex-6.2.2.tgz",
       "integrity": "sha512-Bq3SmSpyFHaWjPk8If9yc6svM8c56dB5BAtW4Qbw5jHTwwXXcTLoRMkpDJp6VL0XzlWaCHTXrkFURMYmD0sLqg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=12"
@@ -6553,6 +6784,7 @@
       "version": "5.6.2",
       "resolved": "https://registry.npmjs.org/chalk/-/chalk-5.6.2.tgz",
       "integrity": "sha512-7NzBL0rN6fMUW+f7A6Io4h40qQlG+xGmtMxfbnH/K7TAtt8JQWVQK+6g0UXKMeVJoyV5EkkNsErQ8pVD3bLHbA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": "^12.17.0 || ^14.13 || >=16.0.0"
@@ -6565,12 +6797,14 @@
       "version": "10.6.0",
       "resolved": "https://registry.npmjs.org/emoji-regex/-/emoji-regex-10.6.0.tgz",
       "integrity": "sha512-toUI84YS5YmxW219erniWD0CIVOo46xGKColeNQRgOzDorgBi1v4D71/OFzgD9GO2UGKIv1C3Sp8DAn0+j5w7A==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/ora/node_modules/string-width": {
       "version": "7.2.0",
       "resolved": "https://registry.npmjs.org/string-width/-/string-width-7.2.0.tgz",
       "integrity": "sha512-tsaTIkKW9b4N+AEj+SVA+WhJzV7/zMhcSu78mLKWSk7cXMOSHsBKFWUs0fWwq8QyK3MgJBQRX6Gbi4kYbdvGkQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "emoji-regex": "^10.3.0",
@@ -6588,6 +6822,7 @@
       "version": "7.2.0",
       "resolved": "https://registry.npmjs.org/strip-ansi/-/strip-ansi-7.2.0.tgz",
       "integrity": "sha512-yDPMNjp4WyfYBkHnjIRLfca1i6KMyGCtsVgoKe/z1+6vukgaENdgGBZt+ZmKPc4gavvEZ5OgHfHdrazhgNyG7w==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "ansi-regex": "^6.2.2"
@@ -6603,6 +6838,7 @@
       "version": "1.4.3",
       "resolved": "https://registry.npmjs.org/outvariant/-/outvariant-1.4.3.tgz",
       "integrity": "sha512-+Sl2UErvtsoajRDKCE5/dBz4DIvHXQQnAxtQTF04OJxY0+DyZXSo5P5Bb7XYWOh81syohlYL24hbDwxedPUJCA==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/p-limit": {
@@ -6641,6 +6877,7 @@
       "version": "1.0.1",
       "resolved": "https://registry.npmjs.org/parent-module/-/parent-module-1.0.1.tgz",
       "integrity": "sha512-GQ2EWRpQV8/o+Aw8YqtfZZPfNRWZYkbidE9k5rpl/hC3vtHHBfGm2Ifi6qWV+coDGkrUKZAxE3Lot5kcsRlh+g==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "callsites": "^3.0.0"
@@ -6653,6 +6890,7 @@
       "version": "5.2.0",
       "resolved": "https://registry.npmjs.org/parse-json/-/parse-json-5.2.0.tgz",
       "integrity": "sha512-ayCKvm/phCGxOkYRSCM82iDwct8/EonSEgCSxWxD7ve6jHggsFl4fZVQBPRNgQoKiuV/odhFrGzQXZwbifC8Rg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/code-frame": "^7.0.0",
@@ -6671,6 +6909,7 @@
       "version": "4.0.0",
       "resolved": "https://registry.npmjs.org/parse-ms/-/parse-ms-4.0.0.tgz",
       "integrity": "sha512-TXfryirbmq34y8QBwgqCVLi+8oA3oWx2eAnSn62ITyEhEYaWRlVZ2DvMM9eZbMs/RfxPu/PK/aBLyGj4IrqMHw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -6696,6 +6935,7 @@
       "version": "1.3.3",
       "resolved": "https://registry.npmjs.org/parseurl/-/parseurl-1.3.3.tgz",
       "integrity": "sha512-CiyeOxFT/JZyN5m0z9PfXw4SCBJ6Sygz1Dpl0wqjlhDEGGBP1GnsUVEL0p63hoG1fcj3fHynXi9NYO4nWOL+qQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.8"
@@ -6705,6 +6945,7 @@
       "version": "1.0.1",
       "resolved": "https://registry.npmjs.org/path-browserify/-/path-browserify-1.0.1.tgz",
       "integrity": "sha512-b7uo2UCUOYZcnF/3ID0lulOJi/bafxa1xPe7ZPsammBSpjSWQkjNxlt635YGS2MiR9GjvuXCtz2emr3jbsz98g==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/path-exists": {
@@ -6721,6 +6962,7 @@
       "version": "3.1.1",
       "resolved": "https://registry.npmjs.org/path-key/-/path-key-3.1.1.tgz",
       "integrity": "sha512-ojmeN0qd+y0jszEtoY48r0Peq5dwMEkIlCOu6Q5f41lfkswXuKtYrhgoTpLnyIcHm24Uhqx+5Tqm2InSwLhE6Q==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=8"
@@ -6730,6 +6972,7 @@
       "version": "6.3.0",
       "resolved": "https://registry.npmjs.org/path-to-regexp/-/path-to-regexp-6.3.0.tgz",
       "integrity": "sha512-Yhpw4T9C6hPpgPeA28us07OJeqZ5EzQTkbfwuhsUg0c237RomFoETJgmp2sa3F/41gfLE6G5cqcYwznmeEeOlQ==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/pathe": {
@@ -6761,6 +7004,7 @@
       "version": "5.0.1",
       "resolved": "https://registry.npmjs.org/pkce-challenge/-/pkce-challenge-5.0.1.tgz",
       "integrity": "sha512-wQ0b/W4Fr01qtpHlqSqspcj3EhBvimsdh0KlHhH8HRZnMsEa0ea2fTULOXOS9ccQr3om+GcGRk4e+isrZWV8qQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=16.20.0"
@@ -6798,6 +7042,7 @@
       "version": "7.1.1",
       "resolved": "https://registry.npmjs.org/postcss-selector-parser/-/postcss-selector-parser-7.1.1.tgz",
       "integrity": "sha512-orRsuYpJVw8LdAwqqLykBj9ecS5/cRHlI5+nvTo8LcCKmzDmqVORXtOIYEEQuL9D4BxtA1lm5isAqzQZCoQ6Eg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "cssesc": "^3.0.0",
@@ -6811,6 +7056,7 @@
       "version": "0.1.0",
       "resolved": "https://registry.npmjs.org/powershell-utils/-/powershell-utils-0.1.0.tgz",
       "integrity": "sha512-dM0jVuXJPsDN6DvRpea484tCUaMiXWjuCn++HGTqUWzGDjv5tZkEZldAJ/UMlqRYGFrD/etByo4/xOuC/snX2A==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=20"
@@ -6863,6 +7109,7 @@
       "version": "9.3.0",
       "resolved": "https://registry.npmjs.org/pretty-ms/-/pretty-ms-9.3.0.tgz",
       "integrity": "sha512-gjVS5hOP+M3wMm5nmNOucbIrqudzs9v/57bWRHQWLYklXqoXKrVfYW2W9+glfGsqtPgpiz5WwyEEB+ksXIx3gQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "parse-ms": "^4.0.0"
@@ -6878,6 +7125,7 @@
       "version": "2.4.2",
       "resolved": "https://registry.npmjs.org/prompts/-/prompts-2.4.2.tgz",
       "integrity": "sha512-NxNv/kLguCA7p3jE8oL2aEBsrJWgAakBpgmgK6lpPWV+WuOmY6r2/zbAVnP+T8bQlA0nzHXSJSJW0Hq7ylaD2Q==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "kleur": "^3.0.3",
@@ -6891,6 +7139,7 @@
       "version": "3.0.3",
       "resolved": "https://registry.npmjs.org/kleur/-/kleur-3.0.3.tgz",
       "integrity": "sha512-eTIzlVOSUR+JxdDFepEYcBMtZ9Qqdef+rnzWdRZuMbOywu5tO2w2N7rqjoANZ5k9vywhL6Br1VRjUIgTQx4E8w==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=6"
@@ -6900,6 +7149,7 @@
       "version": "2.0.7",
       "resolved": "https://registry.npmjs.org/proxy-addr/-/proxy-addr-2.0.7.tgz",
       "integrity": "sha512-llQsMLSUDUPT44jdrU/O37qlnifitDP+ZwrmmZcoSKyLKvtZxpyV0n2/bD/N4tBAAZ/gJEdZU7KMraoK1+XYAg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "forwarded": "0.2.0",
@@ -6943,6 +7193,7 @@
       "version": "6.15.1",
       "resolved": "https://registry.npmjs.org/qs/-/qs-6.15.1.tgz",
       "integrity": "sha512-6YHEFRL9mfgcAvql/XhwTvf5jKcOiiupt2FiJxHkiX1z4j7WL8J/jRHYLluORvc1XxB5rV20KoeK00gVJamspg==",
+      "dev": true,
       "license": "BSD-3-Clause",
       "dependencies": {
         "side-channel": "^1.1.0"
@@ -6964,6 +7215,7 @@
       "version": "1.2.3",
       "resolved": "https://registry.npmjs.org/queue-microtask/-/queue-microtask-1.2.3.tgz",
       "integrity": "sha512-NuaNSa6flKT5JaSYQzJok04JzTL1CA6aGhv5rfLW3PgqA+M2ChpZQnAC8h8i4ZFkBS8X5RqkDBHA7r4hej3K9A==",
+      "dev": true,
       "funding": [
         {
           "type": "github",
@@ -6984,6 +7236,7 @@
       "version": "1.2.1",
       "resolved": "https://registry.npmjs.org/range-parser/-/range-parser-1.2.1.tgz",
       "integrity": "sha512-Hrgsx+orqoygnmhFbKaHE6c296J+HTAQXoxEF6gNupROmmGJRoyzfG3ccAveqCBrwr/2yxQ5BVd/GTl5agOwSg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.6"
@@ -6993,6 +7246,7 @@
       "version": "3.0.2",
       "resolved": "https://registry.npmjs.org/raw-body/-/raw-body-3.0.2.tgz",
       "integrity": "sha512-K5zQjDllxWkf7Z5xJdV0/B0WTNqx6vxG70zJE4N0kBs4LovmEYWJzQGxC9bS9RAKu3bgM40lrd5zoLJ12MQ5BA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "bytes": "~3.1.2",
@@ -7049,6 +7303,44 @@
       "license": "MIT",
       "peer": true
     },
+    "node_modules/react-router": {
+      "version": "7.14.1",
+      "resolved": "https://registry.npmjs.org/react-router/-/react-router-7.14.1.tgz",
+      "integrity": "sha512-5BCvFskyAAVumqhEKh/iPhLOIkfxcEUz8WqFIARCkMg8hZZzDYX9CtwxXA0e+qT8zAxmMC0x3Ckb9iMONwc5jg==",
+      "license": "MIT",
+      "dependencies": {
+        "cookie": "^1.0.1",
+        "set-cookie-parser": "^2.6.0"
+      },
+      "engines": {
+        "node": ">=20.0.0"
+      },
+      "peerDependencies": {
+        "react": ">=18",
+        "react-dom": ">=18"
+      },
+      "peerDependenciesMeta": {
+        "react-dom": {
+          "optional": true
+        }
+      }
+    },
+    "node_modules/react-router-dom": {
+      "version": "7.14.1",
+      "resolved": "https://registry.npmjs.org/react-router-dom/-/react-router-dom-7.14.1.tgz",
+      "integrity": "sha512-ZkrQuwwhGibjQLqH1eCdyiZyLWglPxzxdl5tgwgKEyCSGC76vmAjleGocRe3J/MLfzMUIKwaFJWpFVJhK3d2xA==",
+      "license": "MIT",
+      "dependencies": {
+        "react-router": "7.14.1"
+      },
+      "engines": {
+        "node": ">=20.0.0"
+      },
+      "peerDependencies": {
+        "react": ">=18",
+        "react-dom": ">=18"
+      }
+    },
     "node_modules/react-window": {
       "version": "2.2.7",
       "resolved": "https://registry.npmjs.org/react-window/-/react-window-2.2.7.tgz",
@@ -7063,6 +7355,7 @@
       "version": "0.23.11",
       "resolved": "https://registry.npmjs.org/recast/-/recast-0.23.11.tgz",
       "integrity": "sha512-YTUo+Flmw4ZXiWfQKGcwwc11KnoRAYgzAE2E7mXKCjSviTKShtxBsN6YUUBB2gtaBzKzeKunxhUwNHQuRryhWA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "ast-types": "^0.16.1",
@@ -7093,6 +7386,7 @@
       "version": "2.1.1",
       "resolved": "https://registry.npmjs.org/require-directory/-/require-directory-2.1.1.tgz",
       "integrity": "sha512-fGxEI7+wsG9xrvdjsrlmL22OMTTiHRwAMroiEeMgq8gzoLC/PQr7RsRDSTLUg/bZAZtF+TVIkHc6/4RIKrui+Q==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=0.10.0"
@@ -7102,6 +7396,7 @@
       "version": "2.0.2",
       "resolved": "https://registry.npmjs.org/require-from-string/-/require-from-string-2.0.2.tgz",
       "integrity": "sha512-Xf0nWe6RseziFMu+Ap9biiUbmplq6S9/p+7w7YXP/JBHhrUDDUhwa+vANyubuqfZWTveU//DYVGsDG7RKL/vEw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=0.10.0"
@@ -7123,6 +7418,7 @@
       "version": "4.0.0",
       "resolved": "https://registry.npmjs.org/resolve-from/-/resolve-from-4.0.0.tgz",
       "integrity": "sha512-pb/MYmXstAkysRFx8piNI1tGFNQIFA3vkE3Gq4EuA1dF6gHp/+vgZqsCGJapvy8N3Q+4o7FwvquPJcnZ7RYy4g==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=4"
@@ -7132,6 +7428,7 @@
       "version": "5.1.0",
       "resolved": "https://registry.npmjs.org/restore-cursor/-/restore-cursor-5.1.0.tgz",
       "integrity": "sha512-oMA2dcrw6u0YfxJQXm342bFKX/E4sG9rbTzO9ptUcR/e8A33cHuvStiYOwH7fszkZlZ1z/ta9AAoPk2F4qIOHA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "onetime": "^7.0.0",
@@ -7148,12 +7445,14 @@
       "version": "0.11.7",
       "resolved": "https://registry.npmjs.org/rettime/-/rettime-0.11.7.tgz",
       "integrity": "sha512-DoAm1WjR1eH7z8sHPtvvUMIZh4/CSKkGCz6CxPqOrEAnOGtOuHSnSE9OC+razqxKuf4ub7pAYyl/vZV0vGs5tg==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/reusify": {
       "version": "1.1.0",
       "resolved": "https://registry.npmjs.org/reusify/-/reusify-1.1.0.tgz",
       "integrity": "sha512-g6QUff04oZpHs0eG5p83rFLhHeV00ug/Yf9nZM6fLeUrPguBTkTQOdpAWWspMh55TZfVQDPaN3NQJfbVRAxdIw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "iojs": ">=1.0.0",
@@ -7203,6 +7502,7 @@
       "version": "2.2.0",
       "resolved": "https://registry.npmjs.org/router/-/router-2.2.0.tgz",
       "integrity": "sha512-nLTrUKm2UyiL7rlhapu/Zl45FwNgkZGaCpZbIHajDYgwlJCOzLSk+cIPAnsEqV955GjILJnKbdQC1nVPz+gAYQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "debug": "^4.4.0",
@@ -7219,6 +7519,7 @@
       "version": "8.4.2",
       "resolved": "https://registry.npmjs.org/path-to-regexp/-/path-to-regexp-8.4.2.tgz",
       "integrity": "sha512-qRcuIdP69NPm4qbACK+aDogI5CBDMi1jKe0ry5rSQJz8JVLsC7jV8XpiJjGRLLol3N+R5ihGYcrPLTno6pAdBA==",
+      "dev": true,
       "license": "MIT",
       "funding": {
         "type": "opencollective",
@@ -7229,6 +7530,7 @@
       "version": "7.1.0",
       "resolved": "https://registry.npmjs.org/run-applescript/-/run-applescript-7.1.0.tgz",
       "integrity": "sha512-DPe5pVFaAsinSaV6QjQ6gdiedWDcRCbUuiQfQa2wmWV7+xC9bGulGI8+TdRmoFkAPaBXk8CrAbnlY2ISniJ47Q==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -7241,6 +7543,7 @@
       "version": "1.2.0",
       "resolved": "https://registry.npmjs.org/run-parallel/-/run-parallel-1.2.0.tgz",
       "integrity": "sha512-5l4VyZR86LZ/lDxZTR6jqL8AFE2S0IFLMP26AbjsLVADxHdhB/c0GUsH+y39UfCi3dzz8OlQuPmnaJOMoDHQBA==",
+      "dev": true,
       "funding": [
         {
           "type": "github",
@@ -7264,6 +7567,7 @@
       "version": "7.8.2",
       "resolved": "https://registry.npmjs.org/rxjs/-/rxjs-7.8.2.tgz",
       "integrity": "sha512-dhKf903U/PQZY6boNNtAGdWbG85WAbjT/1xYoZIC7FAY0yWapOBQVsVrDl58W86//e1VpMNBtRV4MaXfdMySFA==",
+      "dev": true,
       "license": "Apache-2.0",
       "dependencies": {
         "tslib": "^2.1.0"
@@ -7273,6 +7577,7 @@
       "version": "2.1.2",
       "resolved": "https://registry.npmjs.org/safer-buffer/-/safer-buffer-2.1.2.tgz",
       "integrity": "sha512-YZo3K82SD7Riyi0E1EQPojLz7kpepnSQI9IyPbHHg1XXXevb5dJI7tpyN2ADxGcQbHG7vcyRHk0cbwqcQriUtg==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/saxes": {
@@ -7298,6 +7603,7 @@
       "version": "6.3.1",
       "resolved": "https://registry.npmjs.org/semver/-/semver-6.3.1.tgz",
       "integrity": "sha512-BR7VvDCVHO+q2xBEWskxS6DJE1qRnb7DxzUrogb71CWoSficBxYsiAGd+Kl0mmq/MprG9yArRkyrQxTO6XjMzA==",
+      "dev": true,
       "license": "ISC",
       "bin": {
         "semver": "bin/semver.js"
@@ -7307,6 +7613,7 @@
       "version": "1.2.1",
       "resolved": "https://registry.npmjs.org/send/-/send-1.2.1.tgz",
       "integrity": "sha512-1gnZf7DFcoIcajTjTwjwuDjzuz4PPcY2StKPlsGAQ1+YH20IRVrBaXSWmdjowTJ6u8Rc01PoYOGHXfP1mYcZNQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "debug": "^4.4.3",
@@ -7333,6 +7640,7 @@
       "version": "1.54.0",
       "resolved": "https://registry.npmjs.org/mime-db/-/mime-db-1.54.0.tgz",
       "integrity": "sha512-aU5EJuIN2WDemCcAp2vFBfp/m4EAhWJnUNSSw0ixs7/kXbd6Pg64EmwJkNdFhB8aWt1sH2CTXrLxo/iAGV3oPQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.6"
@@ -7342,6 +7650,7 @@
       "version": "3.0.2",
       "resolved": "https://registry.npmjs.org/mime-types/-/mime-types-3.0.2.tgz",
       "integrity": "sha512-Lbgzdk0h4juoQ9fCKXW4by0UJqj+nOOrI9MJ1sSj4nI8aI2eo1qmvQEie4VD1glsS250n15LsWsYtCugiStS5A==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "mime-db": "^1.54.0"
@@ -7358,6 +7667,7 @@
       "version": "2.2.1",
       "resolved": "https://registry.npmjs.org/serve-static/-/serve-static-2.2.1.tgz",
       "integrity": "sha512-xRXBn0pPqQTVQiC8wyQrKs2MOlX24zQ0POGaj0kultvoOCstBQM5yvOhAVSUwOMjQtTvsPWoNCHfPGwaaQJhTw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "encodeurl": "^2.0.0",
@@ -7383,12 +7693,14 @@
       "version": "1.2.0",
       "resolved": "https://registry.npmjs.org/setprototypeof/-/setprototypeof-1.2.0.tgz",
       "integrity": "sha512-E5LDX7Wrp85Kil5bhZv46j8jOeboKq5JMmYM3gVGdGH8xFpPWXUMsNrlODCrkoxMEeNi/XZIwuRvY4XNwYMJpw==",
+      "dev": true,
       "license": "ISC"
     },
     "node_modules/shadcn": {
       "version": "4.2.0",
       "resolved": "https://registry.npmjs.org/shadcn/-/shadcn-4.2.0.tgz",
       "integrity": "sha512-ZDuV340itidaUd4Gi1BxQX+Y7Ush6BHp6URZBM2RyxUUBZ6yFtOWIr4nVY+Ro+YRSpo82v7JrsmtcU5xoBCMJQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@babel/core": "^7.28.0",
@@ -7434,6 +7746,7 @@
       "version": "3.3.2",
       "resolved": "https://registry.npmjs.org/node-fetch/-/node-fetch-3.3.2.tgz",
       "integrity": "sha512-dRB78srN/l6gqWulah9SrxeYnxeddIG30+GOqK/9OlLVyLg3HPnr6SqOWTWOXKRwC2eGYCkZ59NNuSgvSrpgOA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "data-uri-to-buffer": "^4.0.0",
@@ -7452,6 +7765,7 @@
       "version": "3.25.76",
       "resolved": "https://registry.npmjs.org/zod/-/zod-3.25.76.tgz",
       "integrity": "sha512-gzUt/qt81nXsFGKIFcC3YnfEAx5NkunCfnDlvuBSSFS02bcXu4Lmea0AFIUwbLWxWPx3d9p8S5QoaujKcNQxcQ==",
+      "dev": true,
       "license": "MIT",
       "funding": {
         "url": "https://github.com/sponsors/colinhacks"
@@ -7461,6 +7775,7 @@
       "version": "2.0.0",
       "resolved": "https://registry.npmjs.org/shebang-command/-/shebang-command-2.0.0.tgz",
       "integrity": "sha512-kHxr2zZpYtdmrN1qDjrrX/Z1rR1kG8Dx+gkpK1G4eXmvXswmcE1hTWBWYUzlraYw1/yZp6YuDY77YtvbN0dmDA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "shebang-regex": "^3.0.0"
@@ -7473,6 +7788,7 @@
       "version": "3.0.0",
       "resolved": "https://registry.npmjs.org/shebang-regex/-/shebang-regex-3.0.0.tgz",
       "integrity": "sha512-7++dFhtcx3353uBaq8DDR4NuxBetBzC7ZQOhmTQInHEd6bSrXdiEyzCvG07Z44UYdLShWUyXt5M/yhz8ekcb1A==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=8"
@@ -7482,6 +7798,7 @@
       "version": "1.8.3",
       "resolved": "https://registry.npmjs.org/shell-quote/-/shell-quote-1.8.3.tgz",
       "integrity": "sha512-ObmnIF4hXNg1BqhnHmgbDETF8dLPCggZWBjkQfhZpbszZnYur5DUljTcCHii5LC3J5E0yeO/1LIMyH+UvHQgyw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.4"
@@ -7494,6 +7811,7 @@
       "version": "1.1.0",
       "resolved": "https://registry.npmjs.org/side-channel/-/side-channel-1.1.0.tgz",
       "integrity": "sha512-ZX99e6tRweoUXqR+VBrslhda51Nh5MTQwou5tnUDgbtyM0dBgmhEDtWGP/xbKn6hqfPRHujUNwz5fy/wbbhnpw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "es-errors": "^1.3.0",
@@ -7513,6 +7831,7 @@
       "version": "1.0.1",
       "resolved": "https://registry.npmjs.org/side-channel-list/-/side-channel-list-1.0.1.tgz",
       "integrity": "sha512-mjn/0bi/oUURjc5Xl7IaWi/OJJJumuoJFQJfDDyO46+hBWsfaVM65TBHq2eoZBhzl9EchxOijpkbRC8SVBQU0w==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "es-errors": "^1.3.0",
@@ -7529,6 +7848,7 @@
       "version": "1.0.1",
       "resolved": "https://registry.npmjs.org/side-channel-map/-/side-channel-map-1.0.1.tgz",
       "integrity": "sha512-VCjCNfgMsby3tTdo02nbjtM/ewra6jPHmpThenkTYh8pG9ucZ/1P8So4u4FGBek/BjpOVsDCMoLA/iuBKIFXRA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "call-bound": "^1.0.2",
@@ -7547,6 +7867,7 @@
       "version": "1.0.2",
       "resolved": "https://registry.npmjs.org/side-channel-weakmap/-/side-channel-weakmap-1.0.2.tgz",
       "integrity": "sha512-WPS/HvHQTYnHisLo9McqBHOJk2FkHO/tlpvldyrnem4aeQp4hai3gythswg6p01oSoTl58rcpiFAjF2br2Ak2A==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "call-bound": "^1.0.2",
@@ -7573,6 +7894,7 @@
       "version": "4.1.0",
       "resolved": "https://registry.npmjs.org/signal-exit/-/signal-exit-4.1.0.tgz",
       "integrity": "sha512-bzyZ1e88w9O1iNJbKnOlvYTrWPDl46O1bG0D3XInv+9tkPrxrN8jUUTiFlDkkmKWgn1M6CfIA13SuGqOa9Korw==",
+      "dev": true,
       "license": "ISC",
       "engines": {
         "node": ">=14"
@@ -7585,12 +7907,14 @@
       "version": "1.0.5",
       "resolved": "https://registry.npmjs.org/sisteransi/-/sisteransi-1.0.5.tgz",
       "integrity": "sha512-bLGGlR1QxBcynn2d5YmDX4MGjlZvy2MRBDRNHLJ8VI6l6+9FUiyTFNJ0IveOSP0bcXgVDPRcfGqA0pjaqUpfVg==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/source-map": {
       "version": "0.6.1",
       "resolved": "https://registry.npmjs.org/source-map/-/source-map-0.6.1.tgz",
       "integrity": "sha512-UjgapumWlbMhkBgzT7Ykc5YXUT46F0iKu8SGXq0bcwP5dz/h0Plj6enJqjz1Zbq2l5WaqYnrVbwWOWMyF3F47g==",
+      "dev": true,
       "license": "BSD-3-Clause",
       "engines": {
         "node": ">=0.10.0"
@@ -7616,6 +7940,7 @@
       "version": "2.0.2",
       "resolved": "https://registry.npmjs.org/statuses/-/statuses-2.0.2.tgz",
       "integrity": "sha512-DvEy55V3DB7uknRo+4iOGT5fP1slR8wQohVdknigZPMpMstaKJQWhwiYBACJE3Ul2pTnATihhBYnRhZQHGBiRw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.8"
@@ -7632,6 +7957,7 @@
       "version": "0.2.2",
       "resolved": "https://registry.npmjs.org/stdin-discarder/-/stdin-discarder-0.2.2.tgz",
       "integrity": "sha512-UhDfHmA92YAlNnCfhmq0VeNL5bDbiZGg7sZ2IvPsXubGkiNa9EC+tUTsjBRsYUAz87btI6/1wf4XoVvQ3uRnmQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -7644,12 +7970,14 @@
       "version": "0.5.1",
       "resolved": "https://registry.npmjs.org/strict-event-emitter/-/strict-event-emitter-0.5.1.tgz",
       "integrity": "sha512-vMgjE/GGEPEFnhFub6pa4FmJBRBVOLpIII2hvCZ8Kzb7K0hlHo7mQv6xYrBvCL2LtAIBwFUK8wvuJgTVSQ5MFQ==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/string-width": {
       "version": "4.2.3",
       "resolved": "https://registry.npmjs.org/string-width/-/string-width-4.2.3.tgz",
       "integrity": "sha512-wKyQRQpjJ0sIp62ErSZdGsjMJWsap5oRNihHhu6G7JVO/9jIB6UyevL+tXuOqrng8j/cxKTWyWUwvSTriiZz/g==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "emoji-regex": "^8.0.0",
@@ -7664,6 +7992,7 @@
       "version": "5.0.0",
       "resolved": "https://registry.npmjs.org/stringify-object/-/stringify-object-5.0.0.tgz",
       "integrity": "sha512-zaJYxz2FtcMb4f+g60KsRNFOpVMUyuJgA51Zi5Z1DOTC3S59+OQiVOzE9GZt0x72uBGWKsQIuBKeF9iusmKFsg==",
+      "dev": true,
       "license": "BSD-2-Clause",
       "dependencies": {
         "get-own-enumerable-keys": "^1.0.0",
@@ -7681,6 +8010,7 @@
       "version": "6.0.1",
       "resolved": "https://registry.npmjs.org/strip-ansi/-/strip-ansi-6.0.1.tgz",
       "integrity": "sha512-Y38VPSHcqkFrCpFnQ9vuSXmquuv5oXOKpGeT6aGrr3o3Gc9AlVa6JBfUSOCnbxGGZF+/0ooI7KrPuUSztUdU5A==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "ansi-regex": "^5.0.1"
@@ -7693,6 +8023,7 @@
       "version": "3.0.0",
       "resolved": "https://registry.npmjs.org/strip-bom/-/strip-bom-3.0.0.tgz",
       "integrity": "sha512-vavAMRXOgBVNF6nyEEmL3DBK19iRpDcoIwW+swQ+CbGiu7lju6t+JklA1MHweoWtadgt4ISVUsXLyDq34ddcwA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=4"
@@ -7702,6 +8033,7 @@
       "version": "4.0.0",
       "resolved": "https://registry.npmjs.org/strip-final-newline/-/strip-final-newline-4.0.0.tgz",
       "integrity": "sha512-aulFJcD6YK8V1G7iRB5tigAP4TsHBZZrOV8pjV++zdUwmeV8uzbY7yn6h9MswN62adStNZFuCIx4haBnRuMDaw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -7740,6 +8072,7 @@
       "version": "8.1.1",
       "resolved": "https://registry.npmjs.org/supports-color/-/supports-color-8.1.1.tgz",
       "integrity": "sha512-MpUEN2OodtUzxvKQl72cUF7RQ5EiHsGvSsVG0ia9c5RbWGL2CI4C7EpPS8UTBIplnlzZiNuV56w+FuNxy3ty2Q==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "has-flag": "^4.0.0"
@@ -7762,6 +8095,7 @@
       "version": "1.0.0",
       "resolved": "https://registry.npmjs.org/tagged-tag/-/tagged-tag-1.0.0.tgz",
       "integrity": "sha512-yEFYrVhod+hdNyx7g5Bnkkb0G6si8HJurOoOEgC8B/O0uXLHlaey/65KRv6cuWBNhBgHKAROVpc7QyYqE5gFng==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=20"
@@ -7803,6 +8137,7 @@
       "version": "1.3.3",
       "resolved": "https://registry.npmjs.org/tiny-invariant/-/tiny-invariant-1.3.3.tgz",
       "integrity": "sha512-+FbBPE1o9QAYvviau/qC5SE3caw21q3xkvWKBtja5vgqOWIHHJ3ioaq1VPfn/Szqctz2bU/oYeKd9/z5BL+PVg==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/tinybench": {
@@ -7852,6 +8187,7 @@
       "version": "7.0.28",
       "resolved": "https://registry.npmjs.org/tldts/-/tldts-7.0.28.tgz",
       "integrity": "sha512-+Zg3vWhRUv8B1maGSTFdev9mjoo8Etn2Ayfs4cnjlD3CsGkxXX4QyW3j2WJ0wdjYcYmy7Lx2RDsZMhgCWafKIw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "tldts-core": "^7.0.28"
@@ -7864,12 +8200,14 @@
       "version": "7.0.28",
       "resolved": "https://registry.npmjs.org/tldts-core/-/tldts-core-7.0.28.tgz",
       "integrity": "sha512-7W5Efjhsc3chVdFhqtaU0KtK32J37Zcr9RKtID54nG+tIpcY79CQK/veYPODxtD/LJ4Lue66jvrQzIX2Z2/pUQ==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/to-regex-range": {
       "version": "5.0.1",
       "resolved": "https://registry.npmjs.org/to-regex-range/-/to-regex-range-5.0.1.tgz",
       "integrity": "sha512-65P7iz6X5yEr1cwcgvQxbbIw7Uk3gOy5dIdtZ4rDveLqhrdJP+Li/Hx6tyK0NEb+2GCyneCMJiGqrADCSNk8sQ==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "is-number": "^7.0.0"
@@ -7882,6 +8220,7 @@
       "version": "1.0.1",
       "resolved": "https://registry.npmjs.org/toidentifier/-/toidentifier-1.0.1.tgz",
       "integrity": "sha512-o5sSPKEkg/DIQNmH43V0/uerLrpzVedkUh8tGNvaeXpfpuwjKenlSox/2O/BTlZUtEe+JG7s5YhEz608PlAHRA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=0.6"
@@ -7912,6 +8251,7 @@
       "version": "1.2.2",
       "resolved": "https://registry.npmjs.org/tree-kill/-/tree-kill-1.2.2.tgz",
       "integrity": "sha512-L0Orpi8qGpRG//Nd+H90vFB+3iHnue1zSSGmNOOCh1GLJ7rUKVwV2HvijphGQS2UmhUZewS9VgvxYIdgr+fG1A==",
+      "dev": true,
       "license": "MIT",
       "bin": {
         "tree-kill": "cli.js"
@@ -7934,6 +8274,7 @@
       "version": "26.0.0",
       "resolved": "https://registry.npmjs.org/ts-morph/-/ts-morph-26.0.0.tgz",
       "integrity": "sha512-ztMO++owQnz8c/gIENcM9XfCEzgoGphTv+nKpYNM1bgsdOVC/jRZuEBf6N+mLLDNg68Kl+GgUZfOySaRiG1/Ug==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "@ts-morph/common": "~0.27.0",
@@ -7944,6 +8285,7 @@
       "version": "4.2.0",
       "resolved": "https://registry.npmjs.org/tsconfig-paths/-/tsconfig-paths-4.2.0.tgz",
       "integrity": "sha512-NoZ4roiN7LnbKn9QqE1amc9DJfzvZXxF4xDavcOWt1BPkdx+m+0gJuPM+S0vCe7zTJMYUP0R8pO2XMr+Y8oLIg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "json5": "^2.2.2",
@@ -7958,6 +8300,7 @@
       "version": "2.8.1",
       "resolved": "https://registry.npmjs.org/tslib/-/tslib-2.8.1.tgz",
       "integrity": "sha512-oJFu94HQb+KVduSUQL7wnpmqnfmLsOA/nAh6b6EH0wCEoK0/mPeXU6c3wKDV83MkOuHPRHtSXKKU99IBazS/2w==",
+      "devOptional": true,
       "license": "0BSD"
     },
     "node_modules/tw-animate-css": {
@@ -7986,6 +8329,7 @@
       "version": "5.5.0",
       "resolved": "https://registry.npmjs.org/type-fest/-/type-fest-5.5.0.tgz",
       "integrity": "sha512-PlBfpQwiUvGViBNX84Yxwjsdhd1TUlXr6zjX7eoirtCPIr08NAmxwa+fcYBTeRQxHo9YC9wwF3m9i700sHma8g==",
+      "dev": true,
       "license": "(MIT OR CC0-1.0)",
       "dependencies": {
         "tagged-tag": "^1.0.0"
@@ -8001,6 +8345,7 @@
       "version": "2.0.1",
       "resolved": "https://registry.npmjs.org/type-is/-/type-is-2.0.1.tgz",
       "integrity": "sha512-OZs6gsjF4vMp32qrCbiVSkrFmXtG/AZhY3t0iAMrMBiAZyV9oALtXO8hsrHbMXF9x6L3grlFuwW2oAz7cav+Gw==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "content-type": "^1.0.5",
@@ -8015,6 +8360,7 @@
       "version": "1.54.0",
       "resolved": "https://registry.npmjs.org/mime-db/-/mime-db-1.54.0.tgz",
       "integrity": "sha512-aU5EJuIN2WDemCcAp2vFBfp/m4EAhWJnUNSSw0ixs7/kXbd6Pg64EmwJkNdFhB8aWt1sH2CTXrLxo/iAGV3oPQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.6"
@@ -8024,6 +8370,7 @@
       "version": "3.0.2",
       "resolved": "https://registry.npmjs.org/mime-types/-/mime-types-3.0.2.tgz",
       "integrity": "sha512-Lbgzdk0h4juoQ9fCKXW4by0UJqj+nOOrI9MJ1sSj4nI8aI2eo1qmvQEie4VD1glsS250n15LsWsYtCugiStS5A==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "mime-db": "^1.54.0"
@@ -8040,7 +8387,7 @@
       "version": "6.0.2",
       "resolved": "https://registry.npmjs.org/typescript/-/typescript-6.0.2.tgz",
       "integrity": "sha512-bGdAIrZ0wiGDo5l8c++HWtbaNCWTS4UTv7RaTH/ThVIgjkveJt83m74bBHMJkuCbslY8ixgLBVZJIOiQlQTjfQ==",
-      "devOptional": true,
+      "dev": true,
       "license": "Apache-2.0",
       "bin": {
         "tsc": "bin/tsc",
@@ -8095,6 +8442,7 @@
       "version": "0.3.0",
       "resolved": "https://registry.npmjs.org/unicorn-magic/-/unicorn-magic-0.3.0.tgz",
       "integrity": "sha512-+QBBXBCvifc56fsbuxZQ6Sic3wqqc3WWaqxs58gvJrcOuN83HGTCwz3oS5phzU9LthRNE9VrJCFCLUgHeeFnfA==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -8116,6 +8464,7 @@
       "version": "1.0.0",
       "resolved": "https://registry.npmjs.org/unpipe/-/unpipe-1.0.0.tgz",
       "integrity": "sha512-pjy2bYhSsufwWlKwPc+l3cN7+wuJlK6uz0YdJEOlQDbl6jo/YlPi4mb8agUkVC8BF7V8NuzeyPNqRksA3hztKQ==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.8"
@@ -8125,6 +8474,7 @@
       "version": "3.0.2",
       "resolved": "https://registry.npmjs.org/until-async/-/until-async-3.0.2.tgz",
       "integrity": "sha512-IiSk4HlzAMqTUseHHe3VhIGyuFmN90zMTpD3Z3y8jeQbzLIq500MVM7Jq2vUAnTKAFPJrqwkzr6PoTcPhGcOiw==",
+      "dev": true,
       "license": "MIT",
       "funding": {
         "url": "https://github.com/sponsors/kettanaito"
@@ -8134,6 +8484,7 @@
       "version": "1.2.3",
       "resolved": "https://registry.npmjs.org/update-browserslist-db/-/update-browserslist-db-1.2.3.tgz",
       "integrity": "sha512-Js0m9cx+qOgDxo0eMiFGEueWztz+d4+M3rGlmKPT+T4IS/jP4ylw3Nwpu6cpTTP8R1MAC1kF4VbdLt3ARf209w==",
+      "dev": true,
       "funding": [
         {
           "type": "opencollective",
@@ -8193,12 +8544,14 @@
       "version": "1.0.2",
       "resolved": "https://registry.npmjs.org/util-deprecate/-/util-deprecate-1.0.2.tgz",
       "integrity": "sha512-EPD5q1uXyFxJpCrLnCc1nHnq3gOa6DZBocAIiI2TaSCA7VCJ1UJDMagCzIkXNsUYfD1daK//LTEQ8xiIbrHtcw==",
+      "dev": true,
       "license": "MIT"
     },
     "node_modules/validate-npm-package-name": {
       "version": "7.0.2",
       "resolved": "https://registry.npmjs.org/validate-npm-package-name/-/validate-npm-package-name-7.0.2.tgz",
       "integrity": "sha512-hVDIBwsRruT73PbK7uP5ebUt+ezEtCmzZz3F59BSr2F6OVFnJ/6h8liuvdLrQ88Xmnk6/+xGGuq+pG9WwTuy3A==",
+      "dev": true,
       "license": "ISC",
       "engines": {
         "node": "^20.17.0 || >=22.9.0"
@@ -8208,6 +8561,7 @@
       "version": "1.1.2",
       "resolved": "https://registry.npmjs.org/vary/-/vary-1.1.2.tgz",
       "integrity": "sha512-BNGbWLfd0eUPabhkXUVm0j8uuvREyTh5ovRa/dyow/BqAbZJyC+5fU+IzQOzmAKzYqYRAISoRhdQr3eIZ/PXqg==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 0.8"
@@ -8397,6 +8751,7 @@
       "version": "3.3.3",
       "resolved": "https://registry.npmjs.org/web-streams-polyfill/-/web-streams-polyfill-3.3.3.tgz",
       "integrity": "sha512-d2JWLCivmZYTSIoge9MsgFCZrt571BikcWGYkjC1khllbTeDlGqZ2D8vD8E/lJa8WGWbb7Plm8/XJYV7IJHZZw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">= 8"
@@ -8432,6 +8787,7 @@
       "version": "2.0.2",
       "resolved": "https://registry.npmjs.org/which/-/which-2.0.2.tgz",
       "integrity": "sha512-BLI3Tl1TW3Pvl70l3yq3Y64i+awpwXqsGBYWkkqMtnbXgrMD+yj7rhW0kuEDxzJaYXGjEW5ogapKNMEKNMjibA==",
+      "dev": true,
       "license": "ISC",
       "dependencies": {
         "isexe": "^2.0.0"
@@ -8474,6 +8830,7 @@
       "version": "7.0.0",
       "resolved": "https://registry.npmjs.org/wrap-ansi/-/wrap-ansi-7.0.0.tgz",
       "integrity": "sha512-YVGIj2kamLSTxw6NsZjoBxfSwsn0ycdesmc4p+Q21c5zPuZ1pl+NfxVdxPtdHvmNVOQ6XSYG4AUtyt/Fi7D16Q==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "ansi-styles": "^4.0.0",
@@ -8491,6 +8848,7 @@
       "version": "1.0.2",
       "resolved": "https://registry.npmjs.org/wrappy/-/wrappy-1.0.2.tgz",
       "integrity": "sha512-l4Sp/DRseor9wL6EvV2+TuQn63dMkPjZ/sp9XkghTEbV9KlPS1xUsZ3u7/IQO4wxtcFB4bgpQPRcR3QCvezPcQ==",
+      "dev": true,
       "license": "ISC"
     },
     "node_modules/ws": {
@@ -8518,6 +8876,7 @@
       "version": "0.3.1",
       "resolved": "https://registry.npmjs.org/wsl-utils/-/wsl-utils-0.3.1.tgz",
       "integrity": "sha512-g/eziiSUNBSsdDJtCLB8bdYEUMj4jR7AGeUo96p/3dTafgjHhpF4RiCFPiRILwjQoDXx5MqkBr4fwWtR3Ky4Wg==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "is-wsl": "^3.1.0",
@@ -8551,6 +8910,7 @@
       "version": "5.0.8",
       "resolved": "https://registry.npmjs.org/y18n/-/y18n-5.0.8.tgz",
       "integrity": "sha512-0pfFzegeDWJHJIAmTLRP2DwHjdF5s7jo9tuztdQxAhINCdvS+3nGINqPd00AphqJR/0LhANUS6/+7SCb98YOfA==",
+      "dev": true,
       "license": "ISC",
       "engines": {
         "node": ">=10"
@@ -8560,12 +8920,14 @@
       "version": "3.1.1",
       "resolved": "https://registry.npmjs.org/yallist/-/yallist-3.1.1.tgz",
       "integrity": "sha512-a4UGQaWPH59mOXUYnAG2ewncQS4i4F43Tv3JoAM+s2VDAmS9NsK8GpDMLrCHPksFT7h3K6TOoUNn2pb7RoXx4g==",
+      "dev": true,
       "license": "ISC"
     },
     "node_modules/yargs": {
       "version": "17.7.2",
       "resolved": "https://registry.npmjs.org/yargs/-/yargs-17.7.2.tgz",
       "integrity": "sha512-7dSzzRQ++CKnNI/krKnYRV7JKKPUXMEh61soaHKg9mrWEhzFWhFnxPxGl+69cD1Ou63C13NUPCnmIcrvqCuM6w==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "cliui": "^8.0.1",
@@ -8584,6 +8946,7 @@
       "version": "21.1.1",
       "resolved": "https://registry.npmjs.org/yargs-parser/-/yargs-parser-21.1.1.tgz",
       "integrity": "sha512-tVpsJW7DdjecAiFpbIB1e3qxIQsE6NoPc5/eTdrbbIC4h0LVsWhnoa3g+m2HclBIujHzsxZ4VJVA+GUuc2/LBw==",
+      "dev": true,
       "license": "ISC",
       "engines": {
         "node": ">=12"
@@ -8606,6 +8969,7 @@
       "version": "1.1.0",
       "resolved": "https://registry.npmjs.org/yocto-spinner/-/yocto-spinner-1.1.0.tgz",
       "integrity": "sha512-/BY0AUXnS7IKO354uLLA2eRcWiqDifEbd6unXCsOxkFDAkhgUL3PH9X2bFoaU0YchnDXsF+iKleeTLJGckbXfA==",
+      "dev": true,
       "license": "MIT",
       "dependencies": {
         "yoctocolors": "^2.1.1"
@@ -8621,6 +8985,7 @@
       "version": "2.1.2",
       "resolved": "https://registry.npmjs.org/yoctocolors/-/yoctocolors-2.1.2.tgz",
       "integrity": "sha512-CzhO+pFNo8ajLM2d2IW/R93ipy99LWjtwblvC1RsoSUMZgyLbYFr221TnSNT7GjGdYui6P459mw9JH/g/zW2ug==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -8633,6 +8998,7 @@
       "version": "2.1.3",
       "resolved": "https://registry.npmjs.org/yoctocolors-cjs/-/yoctocolors-cjs-2.1.3.tgz",
       "integrity": "sha512-U/PBtDf35ff0D8X8D0jfdzHYEPFxAI7jJlxZXwCSez5M3190m+QobIfh+sWDWSHMCWWJN2AWamkegn6vr6YBTw==",
+      "dev": true,
       "license": "MIT",
       "engines": {
         "node": ">=18"
@@ -8654,6 +9020,7 @@
       "version": "3.25.2",
       "resolved": "https://registry.npmjs.org/zod-to-json-schema/-/zod-to-json-schema-3.25.2.tgz",
       "integrity": "sha512-O/PgfnpT1xKSDeQYSCfRI5Gy3hPf91mKVDuYLUHZJMiDFptvP41MSnWofm8dnCm0256ZNfZIM7DSzuSMAFnjHA==",
+      "dev": true,
       "license": "ISC",
       "peerDependencies": {
         "zod": "^3.25.28 || ^4"
diff --git a/src/Content/Presentation/Presentation.WebUI/package.json b/src/Content/Presentation/Presentation.WebUI/package.json
index 9076de2..bf4fc73 100644
--- a/src/Content/Presentation/Presentation.WebUI/package.json
+++ b/src/Content/Presentation/Presentation.WebUI/package.json
@@ -28,6 +28,7 @@
     "react": "^19.2.4",
     "react-dom": "^19.2.4",
     "react-hook-form": "^7.72.1",
+    "react-router-dom": "^7.14.1",
     "react-window": "^2.2.7",
     "tailwind-merge": "^3.5.0",
     "tailwindcss": "^4.2.2",
@@ -46,17 +47,17 @@
     "@types/react-window": "^1.8.8",
     "@vitejs/plugin-react": "^6.0.1",
     "@vitest/coverage-v8": "^4.1.4",
+    "concurrently": "^9.2.1",
     "eslint": "^9.39.4",
     "eslint-plugin-react-hooks": "^7.0.1",
     "eslint-plugin-react-refresh": "^0.5.2",
     "globals": "^17.4.0",
     "jsdom": "^29.0.2",
     "msw": "^2.13.3",
+    "shadcn": "^4.2.0",
     "typescript": "~6.0.2",
     "typescript-eslint": "^8.58.0",
     "vite": "^8.0.4",
-    "concurrently": "^9.2.1",
-    "shadcn": "^4.2.0",
     "vitest": "^4.1.4"
   }
 }
diff --git a/src/Content/Presentation/Presentation.WebUI/src/App.tsx b/src/Content/Presentation/Presentation.WebUI/src/App.tsx
index 46a5992..4de4bf4 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/App.tsx
+++ b/src/Content/Presentation/Presentation.WebUI/src/App.tsx
@@ -1,121 +1 @@
-import { useState } from 'react'
-import reactLogo from './assets/react.svg'
-import viteLogo from './assets/vite.svg'
-import heroImg from './assets/hero.png'
-import './App.css'
-
-function App() {
-  const [count, setCount] = useState(0)
-
-  return (
-    <>
-      <section id="center">
-        <div className="hero">
-          <img src={heroImg} className="base" width="170" height="179" alt="" />
-          <img src={reactLogo} className="framework" alt="React logo" />
-          <img src={viteLogo} className="vite" alt="Vite logo" />
-        </div>
-        <div>
-          <h1>Get started</h1>
-          <p>
-            Edit <code>src/App.tsx</code> and save to test <code>HMR</code>
-          </p>
-        </div>
-        <button
-          className="counter"
-          onClick={() => setCount((count) => count + 1)}
-        >
-          Count is {count}
-        </button>
-      </section>
-
-      <div className="ticks"></div>
-
-      <section id="next-steps">
-        <div id="docs">
-          <svg className="icon" role="presentation" aria-hidden="true">
-            <use href="/icons.svg#documentation-icon"></use>
-          </svg>
-          <h2>Documentation</h2>
-          <p>Your questions, answered</p>
-          <ul>
-            <li>
-              <a href="https://vite.dev/" target="_blank">
-                <img className="logo" src={viteLogo} alt="" />
-                Explore Vite
-              </a>
-            </li>
-            <li>
-              <a href="https://react.dev/" target="_blank">
-                <img className="button-icon" src={reactLogo} alt="" />
-                Learn more
-              </a>
-            </li>
-          </ul>
-        </div>
-        <div id="social">
-          <svg className="icon" role="presentation" aria-hidden="true">
-            <use href="/icons.svg#social-icon"></use>
-          </svg>
-          <h2>Connect with us</h2>
-          <p>Join the Vite community</p>
-          <ul>
-            <li>
-              <a href="https://github.com/vitejs/vite" target="_blank">
-                <svg
-                  className="button-icon"
-                  role="presentation"
-                  aria-hidden="true"
-                >
-                  <use href="/icons.svg#github-icon"></use>
-                </svg>
-                GitHub
-              </a>
-            </li>
-            <li>
-              <a href="https://chat.vite.dev/" target="_blank">
-                <svg
-                  className="button-icon"
-                  role="presentation"
-                  aria-hidden="true"
-                >
-                  <use href="/icons.svg#discord-icon"></use>
-                </svg>
-                Discord
-              </a>
-            </li>
-            <li>
-              <a href="https://x.com/vite_js" target="_blank">
-                <svg
-                  className="button-icon"
-                  role="presentation"
-                  aria-hidden="true"
-                >
-                  <use href="/icons.svg#x-icon"></use>
-                </svg>
-                X.com
-              </a>
-            </li>
-            <li>
-              <a href="https://bsky.app/profile/vite.dev" target="_blank">
-                <svg
-                  className="button-icon"
-                  role="presentation"
-                  aria-hidden="true"
-                >
-                  <use href="/icons.svg#bluesky-icon"></use>
-                </svg>
-                Bluesky
-              </a>
-            </li>
-          </ul>
-        </div>
-      </section>
-
-      <div className="ticks"></div>
-      <section id="spacer"></section>
-    </>
-  )
-}
-
-export default App
+export { default } from './app/App';
diff --git a/src/Content/Presentation/Presentation.WebUI/src/__tests__/App.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/__tests__/App.test.tsx
new file mode 100644
index 0000000..ca1a945
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/__tests__/App.test.tsx
@@ -0,0 +1,46 @@
+import { describe, it, expect, vi } from 'vitest';
+import { render, screen } from '@testing-library/react';
+import App from '@/app/App';
+
+// Use vi.hoisted so the object is available inside the vi.mock factory
+const mocks = vi.hoisted(() => ({ showAuthenticated: { value: true } }));
+
+vi.mock('@azure/msal-react', () => ({
+  MsalProvider: ({ children }: { children: React.ReactNode }) => <>{children}</>,
+  AuthenticatedTemplate: ({ children }: { children: React.ReactNode }) =>
+    mocks.showAuthenticated.value ? <>{children}</> : null,
+  UnauthenticatedTemplate: ({ children }: { children: React.ReactNode }) =>
+    !mocks.showAuthenticated.value ? <>{children}</> : null,
+  useMsal: () => ({
+    instance: {
+      logoutRedirect: vi.fn().mockResolvedValue(undefined),
+      loginRedirect: vi.fn().mockResolvedValue(undefined),
+    },
+    accounts: [{ name: 'Test User', username: 'test@test.com' }],
+    inProgress: 'none',
+  }),
+}));
+
+vi.mock('@/lib/authConfig', () => ({
+  msalConfig: {},
+  loginRequest: { scopes: [] },
+  msalInstance: {
+    initialize: vi.fn().mockResolvedValue(undefined),
+    logoutRedirect: vi.fn().mockResolvedValue(undefined),
+    loginRedirect: vi.fn().mockResolvedValue(undefined),
+  },
+}));
+
+describe('App', () => {
+  it('renders without crashing when MSAL is in authenticated state (mock)', () => {
+    mocks.showAuthenticated.value = true;
+    render(<App />);
+    expect(screen.getByText('AgentHub')).toBeInTheDocument();
+  });
+
+  it('renders login redirect when MSAL is not authenticated', () => {
+    mocks.showAuthenticated.value = false;
+    render(<App />);
+    expect(screen.getByRole('button', { name: /sign in/i })).toBeInTheDocument();
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/__tests__/Header.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/__tests__/Header.test.tsx
new file mode 100644
index 0000000..883ba52
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/__tests__/Header.test.tsx
@@ -0,0 +1,19 @@
+import { describe, it, expect, vi } from 'vitest';
+import { screen } from '@testing-library/react';
+import { Header } from '@/components/layout/Header';
+import { renderWithProviders } from '@/test/utils';
+
+vi.mock('@azure/msal-react', () => ({
+  useMsal: () => ({
+    instance: { logoutRedirect: vi.fn().mockResolvedValue(undefined) },
+    accounts: [{ name: 'Test User', username: 'test@test.com' }],
+    inProgress: 'none',
+  }),
+}));
+
+describe('Header', () => {
+  it('renders app name', () => {
+    renderWithProviders(<Header />);
+    expect(screen.getByText('AgentHub')).toBeInTheDocument();
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/__tests__/SplitPanel.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/__tests__/SplitPanel.test.tsx
new file mode 100644
index 0000000..769c496
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/__tests__/SplitPanel.test.tsx
@@ -0,0 +1,21 @@
+import { describe, it, expect } from 'vitest';
+import { screen } from '@testing-library/react';
+import { SplitPanel } from '@/components/layout/SplitPanel';
+import { renderWithProviders } from '@/test/utils';
+
+describe('SplitPanel', () => {
+  it('renders left and right children', () => {
+    renderWithProviders(
+      <SplitPanel left={<div>Left</div>} right={<div>Right</div>} />
+    );
+    expect(screen.getByText('Left')).toBeInTheDocument();
+    expect(screen.getByText('Right')).toBeInTheDocument();
+  });
+
+  it('left panel is accessible (has landmark role or aria-label)', () => {
+    renderWithProviders(
+      <SplitPanel left={<div />} right={<div />} />
+    );
+    expect(screen.getByRole('main')).toBeInTheDocument();
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/__tests__/ThemeProvider.test.tsx b/src/Content/Presentation/Presentation.WebUI/src/__tests__/ThemeProvider.test.tsx
new file mode 100644
index 0000000..6368034
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/__tests__/ThemeProvider.test.tsx
@@ -0,0 +1,43 @@
+import { describe, it, expect, beforeEach } from 'vitest';
+import { render, screen, fireEvent } from '@testing-library/react';
+import { ThemeProvider, useTheme } from '@/components/theme/ThemeProvider';
+
+function TestConsumer() {
+  const { theme, toggleTheme } = useTheme();
+  return (
+    <div>
+      <span data-testid="theme-value">{theme}</span>
+      <button onClick={toggleTheme}>Toggle</button>
+    </div>
+  );
+}
+
+describe('ThemeProvider', () => {
+  beforeEach(() => {
+    localStorage.clear();
+    delete document.documentElement.dataset['theme'];
+  });
+
+  it('applies data-theme="dark" to html element when dark mode selected', () => {
+    render(
+      <ThemeProvider>
+        <TestConsumer />
+      </ThemeProvider>
+    );
+
+    expect(document.documentElement.dataset['theme']).toBe('light');
+    fireEvent.click(screen.getByRole('button', { name: /toggle/i }));
+    expect(document.documentElement.dataset['theme']).toBe('dark');
+  });
+
+  it('persists theme selection to localStorage', () => {
+    render(
+      <ThemeProvider>
+        <TestConsumer />
+      </ThemeProvider>
+    );
+
+    fireEvent.click(screen.getByRole('button', { name: /toggle/i }));
+    expect(localStorage.getItem('theme')).toBe('dark');
+  });
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/app/App.tsx b/src/Content/Presentation/Presentation.WebUI/src/app/App.tsx
new file mode 100644
index 0000000..ab505f5
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/app/App.tsx
@@ -0,0 +1,10 @@
+import { Providers } from './providers';
+import { AppRouter } from './router';
+
+export default function App() {
+  return (
+    <Providers>
+      <AppRouter />
+    </Providers>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/app/providers.tsx b/src/Content/Presentation/Presentation.WebUI/src/app/providers.tsx
new file mode 100644
index 0000000..650a57e
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/app/providers.tsx
@@ -0,0 +1,22 @@
+import { MsalProvider } from '@azure/msal-react';
+import { QueryClientProvider } from '@tanstack/react-query';
+import type { ReactNode } from 'react';
+import { msalInstance } from '@/lib/authConfig';
+import { queryClient } from '@/lib/queryClient';
+import { ThemeProvider } from '@/components/theme/ThemeProvider';
+
+interface ProvidersProps {
+  children: ReactNode;
+}
+
+export function Providers({ children }: ProvidersProps) {
+  return (
+    <MsalProvider instance={msalInstance}>
+      <QueryClientProvider client={queryClient}>
+        <ThemeProvider>
+          {children}
+        </ThemeProvider>
+      </QueryClientProvider>
+    </MsalProvider>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/app/router.tsx b/src/Content/Presentation/Presentation.WebUI/src/app/router.tsx
new file mode 100644
index 0000000..e8e412a
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/app/router.tsx
@@ -0,0 +1,38 @@
+import { AuthenticatedTemplate, UnauthenticatedTemplate, useMsal } from '@azure/msal-react';
+import { BrowserRouter, Routes, Route } from 'react-router-dom';
+import { AppShell } from '@/components/layout/AppShell';
+
+function LoginView() {
+  const { instance } = useMsal();
+
+  return (
+    <div className="flex items-center justify-center h-screen">
+      <div className="text-center space-y-4">
+        <h1 className="text-2xl font-bold">AgentHub</h1>
+        <p className="text-muted-foreground">Sign in with your Microsoft account to continue</p>
+        <button
+          onClick={() => { void instance.loginRedirect(); }}
+          className="px-4 py-2 bg-primary text-primary-foreground rounded hover:opacity-90"
+        >
+          Sign in with Microsoft
+        </button>
+      </div>
+    </div>
+  );
+}
+
+export function AppRouter() {
+  return (
+    <BrowserRouter>
+      <AuthenticatedTemplate>
+        <Routes>
+          <Route path="/" element={<AppShell />} />
+          <Route path="*" element={<AppShell />} />
+        </Routes>
+      </AuthenticatedTemplate>
+      <UnauthenticatedTemplate>
+        <LoginView />
+      </UnauthenticatedTemplate>
+    </BrowserRouter>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/components/layout/AppShell.tsx b/src/Content/Presentation/Presentation.WebUI/src/components/layout/AppShell.tsx
new file mode 100644
index 0000000..ef1c31d
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/components/layout/AppShell.tsx
@@ -0,0 +1,13 @@
+import { Header } from './Header';
+import { SplitPanel } from './SplitPanel';
+
+export function AppShell() {
+  return (
+    <div className="flex flex-col h-screen overflow-hidden">
+      <Header />
+      <div className="flex-1 overflow-hidden min-h-0">
+        <SplitPanel left={<div />} right={<div />} />
+      </div>
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/components/layout/Header.tsx b/src/Content/Presentation/Presentation.WebUI/src/components/layout/Header.tsx
new file mode 100644
index 0000000..201c43b
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/components/layout/Header.tsx
@@ -0,0 +1,38 @@
+import { Moon, Sun } from 'lucide-react';
+import { useMsal } from '@azure/msal-react';
+import { useTheme } from '@/hooks/useTheme';
+
+export function Header() {
+  const { theme, toggleTheme } = useTheme();
+  const { instance, accounts } = useMsal();
+  const userName = accounts[0]?.name ?? '';
+
+  return (
+    <header className="flex items-center justify-between px-4 h-16 border-b shrink-0">
+      <div className="flex items-center gap-4">
+        <span className="font-semibold text-lg">AgentHub</span>
+        <select disabled aria-label="Select agent" className="text-sm opacity-50 cursor-not-allowed">
+          <option>No agent selected</option>
+        </select>
+      </div>
+      <div className="flex items-center gap-2">
+        <button
+          onClick={toggleTheme}
+          aria-label="Toggle theme"
+          className="p-2 rounded hover:bg-accent"
+        >
+          {theme === 'dark' ? <Sun size={16} /> : <Moon size={16} />}
+        </button>
+        {userName && (
+          <span className="text-sm text-muted-foreground">{userName}</span>
+        )}
+        <button
+          onClick={() => { void instance.logoutRedirect(); }}
+          className="text-sm px-3 py-1 rounded border hover:bg-accent"
+        >
+          Sign out
+        </button>
+      </div>
+    </header>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/components/layout/SplitPanel.tsx b/src/Content/Presentation/Presentation.WebUI/src/components/layout/SplitPanel.tsx
new file mode 100644
index 0000000..f0b1c15
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/components/layout/SplitPanel.tsx
@@ -0,0 +1,55 @@
+import { useRef, useCallback, type ReactNode } from 'react';
+
+interface SplitPanelProps {
+  left: ReactNode;
+  right: ReactNode;
+}
+
+export function SplitPanel({ left, right }: SplitPanelProps) {
+  const containerRef = useRef<HTMLDivElement>(null);
+
+  const handleDividerMouseDown = useCallback((e: React.MouseEvent) => {
+    const container = containerRef.current;
+    if (!container) return;
+    e.preventDefault();
+
+    const onMouseMove = (moveEvent: MouseEvent) => {
+      const rect = container.getBoundingClientRect();
+      const fraction = (moveEvent.clientX - rect.left) / rect.width;
+      const clamped = Math.max(0.2, Math.min(0.8, fraction));
+      container.style.setProperty('--split-pos', `${clamped * 100}%`);
+    };
+
+    const onMouseUp = () => {
+      document.removeEventListener('mousemove', onMouseMove);
+      document.removeEventListener('mouseup', onMouseUp);
+    };
+
+    document.addEventListener('mousemove', onMouseMove);
+    document.addEventListener('mouseup', onMouseUp);
+  }, []);
+
+  return (
+    <div
+      ref={containerRef}
+      style={{
+        display: 'grid',
+        gridTemplateColumns: 'var(--split-pos, 40%) 4px 1fr',
+        height: '100%',
+      }}
+    >
+      <main role="main" aria-label="Chat panel" className="overflow-hidden">
+        {left}
+      </main>
+      <div
+        style={{ cursor: 'col-resize' }}
+        className="bg-border"
+        onMouseDown={handleDividerMouseDown}
+        aria-hidden="true"
+      />
+      <div aria-label="Details panel" className="overflow-hidden">
+        {right}
+      </div>
+    </div>
+  );
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/components/theme/ThemeProvider.tsx b/src/Content/Presentation/Presentation.WebUI/src/components/theme/ThemeProvider.tsx
new file mode 100644
index 0000000..7f18ce2
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/components/theme/ThemeProvider.tsx
@@ -0,0 +1,43 @@
+import { createContext, useContext, useEffect, useState, type ReactNode } from 'react';
+
+interface ThemeContextValue {
+  theme: 'light' | 'dark';
+  toggleTheme: () => void;
+}
+
+export const ThemeContext = createContext<ThemeContextValue | undefined>(undefined);
+
+function getInitialTheme(): 'light' | 'dark' {
+  const stored = localStorage.getItem('theme');
+  if (stored === 'light' || stored === 'dark') return stored;
+  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
+}
+
+interface ThemeProviderProps {
+  children: ReactNode;
+}
+
+export function ThemeProvider({ children }: ThemeProviderProps) {
+  const [theme, setTheme] = useState<'light' | 'dark'>(getInitialTheme);
+
+  useEffect(() => {
+    document.documentElement.dataset['theme'] = theme;
+    localStorage.setItem('theme', theme);
+  }, [theme]);
+
+  const toggleTheme = () => setTheme((t) => (t === 'light' ? 'dark' : 'light'));
+
+  return (
+    <ThemeContext.Provider value={{ theme, toggleTheme }}>
+      {children}
+    </ThemeContext.Provider>
+  );
+}
+
+export function useTheme(): ThemeContextValue {
+  const context = useContext(ThemeContext);
+  if (!context) {
+    throw new Error('useTheme must be used within a ThemeProvider');
+  }
+  return context;
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.ts b/src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.ts
new file mode 100644
index 0000000..97697a4
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/hooks/useAgentHub.ts
@@ -0,0 +1,19 @@
+// Full implementation added in section 09
+
+export interface AgentHubState {
+  connectionState: 'disconnected' | 'connecting' | 'connected';
+  sendMessage: (_conversationId: string, _message: string) => void;
+  startConversation: (_conversationId: string) => void;
+  endConversation: (_conversationId: string) => void;
+  joinGlobalTraces: () => void;
+}
+
+export default function useAgentHub(): AgentHubState {
+  return {
+    connectionState: 'disconnected',
+    sendMessage: () => {},
+    startConversation: () => {},
+    endConversation: () => {},
+    joinGlobalTraces: () => {},
+  };
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/hooks/useTheme.ts b/src/Content/Presentation/Presentation.WebUI/src/hooks/useTheme.ts
new file mode 100644
index 0000000..177a454
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/hooks/useTheme.ts
@@ -0,0 +1 @@
+export { useTheme } from '@/components/theme/ThemeProvider';
diff --git a/src/Content/Presentation/Presentation.WebUI/src/index.css b/src/Content/Presentation/Presentation.WebUI/src/index.css
index 3c5ba57..c07de8a 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/index.css
+++ b/src/Content/Presentation/Presentation.WebUI/src/index.css
@@ -3,7 +3,7 @@
 @import "shadcn/tailwind.css";
 @import "@fontsource-variable/geist";
 
-@custom-variant dark (&:is(.dark *));
+@custom-variant dark (&:where([data-theme="dark"], [data-theme="dark"] *));
 
 :root {
   --text: #6b6375;
@@ -220,7 +220,7 @@ code {
   --radius-4xl: calc(var(--radius) * 2.6);
 }
 
-.dark {
+[data-theme="dark"], .dark {
   --background: oklch(0.145 0 0);
   --foreground: oklch(0.985 0 0);
   --card: oklch(0.205 0 0);
diff --git a/src/Content/Presentation/Presentation.WebUI/src/lib/apiClient.ts b/src/Content/Presentation/Presentation.WebUI/src/lib/apiClient.ts
new file mode 100644
index 0000000..7abeb0d
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/lib/apiClient.ts
@@ -0,0 +1,8 @@
+import axios from 'axios';
+
+// Token interceptor added in section 09
+const apiClient = axios.create({
+  baseURL: import.meta.env['VITE_API_BASE_URL'] as string | undefined,
+});
+
+export default apiClient;
diff --git a/src/Content/Presentation/Presentation.WebUI/src/lib/authConfig.ts b/src/Content/Presentation/Presentation.WebUI/src/lib/authConfig.ts
new file mode 100644
index 0000000..2105b18
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/lib/authConfig.ts
@@ -0,0 +1,19 @@
+import type { Configuration, PopupRequest } from '@azure/msal-browser';
+import { PublicClientApplication } from '@azure/msal-browser';
+
+export const msalConfig: Configuration = {
+  auth: {
+    clientId: (import.meta.env['VITE_AZURE_CLIENT_ID'] as string | undefined) ?? '',
+    authority: `https://login.microsoftonline.com/${(import.meta.env['VITE_AZURE_TENANT_ID'] as string | undefined) ?? 'common'}`,
+    redirectUri: window.location.origin,
+  },
+  cache: {
+    cacheLocation: 'sessionStorage',
+  },
+};
+
+export const loginRequest: PopupRequest = {
+  scopes: [`api://${(import.meta.env['VITE_AZURE_CLIENT_ID'] as string | undefined) ?? ''}/.default`],
+};
+
+export const msalInstance = new PublicClientApplication(msalConfig);
diff --git a/src/Content/Presentation/Presentation.WebUI/src/lib/queryClient.ts b/src/Content/Presentation/Presentation.WebUI/src/lib/queryClient.ts
new file mode 100644
index 0000000..219cde6
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/lib/queryClient.ts
@@ -0,0 +1,9 @@
+import { QueryClient } from '@tanstack/react-query';
+
+export const queryClient = new QueryClient({
+  defaultOptions: {
+    queries: {
+      staleTime: 1000 * 60 * 5,
+    },
+  },
+});
diff --git a/src/Content/Presentation/Presentation.WebUI/src/lib/signalrClient.ts b/src/Content/Presentation/Presentation.WebUI/src/lib/signalrClient.ts
new file mode 100644
index 0000000..c6cc97c
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/lib/signalrClient.ts
@@ -0,0 +1,6 @@
+import type { HubConnection } from '@microsoft/signalr';
+
+// Full implementation added in section 09
+export function buildHubConnection(_url: string): HubConnection {
+  throw new Error('Not implemented — replaced in section 09');
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/main.tsx b/src/Content/Presentation/Presentation.WebUI/src/main.tsx
index bef5202..5676dea 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/main.tsx
+++ b/src/Content/Presentation/Presentation.WebUI/src/main.tsx
@@ -1,7 +1,7 @@
 import { StrictMode } from 'react'
 import { createRoot } from 'react-dom/client'
 import './index.css'
-import App from './App.tsx'
+import App from './app/App'
 
 createRoot(document.getElementById('root')!).render(
   <StrictMode>
diff --git a/src/Content/Presentation/Presentation.WebUI/src/stores/appStore.ts b/src/Content/Presentation/Presentation.WebUI/src/stores/appStore.ts
new file mode 100644
index 0000000..5855333
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/stores/appStore.ts
@@ -0,0 +1,11 @@
+import { create } from 'zustand';
+
+interface AppState {
+  selectedAgent: string | null;
+  setSelectedAgent: (name: string) => void;
+}
+
+export const useAppStore = create<AppState>()((set) => ({
+  selectedAgent: null,
+  setSelectedAgent: (name) => set({ selectedAgent: name }),
+}));
diff --git a/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts b/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts
index 4b22e31..7ed5cad 100644
--- a/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts
+++ b/src/Content/Presentation/Presentation.WebUI/src/test/setup.ts
@@ -1 +1,19 @@
-// MSW server and jest-dom setup added in section 12
+import '@testing-library/jest-dom';
+import { vi } from 'vitest';
+
+// Mock matchMedia — not implemented in jsdom
+Object.defineProperty(window, 'matchMedia', {
+  writable: true,
+  value: vi.fn().mockImplementation((query: string) => ({
+    matches: false,
+    media: query,
+    onchange: null,
+    addListener: vi.fn(),
+    removeListener: vi.fn(),
+    addEventListener: vi.fn(),
+    removeEventListener: vi.fn(),
+    dispatchEvent: vi.fn(),
+  })),
+});
+
+// MSW server setup added in section 12
diff --git a/src/Content/Presentation/Presentation.WebUI/src/test/utils.tsx b/src/Content/Presentation/Presentation.WebUI/src/test/utils.tsx
new file mode 100644
index 0000000..9ae2bcd
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/test/utils.tsx
@@ -0,0 +1,39 @@
+import { render, type RenderOptions } from '@testing-library/react';
+import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
+import { MemoryRouter } from 'react-router-dom';
+import type { ReactElement, ReactNode } from 'react';
+import { ThemeProvider } from '@/components/theme/ThemeProvider';
+
+function createTestQueryClient() {
+  return new QueryClient({
+    defaultOptions: {
+      queries: { retry: false },
+      mutations: { retry: false },
+    },
+  });
+}
+
+interface RenderWithProvidersOptions extends Omit<RenderOptions, 'wrapper'> {
+  route?: string;
+}
+
+export function renderWithProviders(
+  ui: ReactElement,
+  { route = '/', ...renderOptions }: RenderWithProvidersOptions = {}
+) {
+  const testQueryClient = createTestQueryClient();
+
+  function Wrapper({ children }: { children: ReactNode }) {
+    return (
+      <MemoryRouter initialEntries={[route]}>
+        <QueryClientProvider client={testQueryClient}>
+          <ThemeProvider>
+            {children}
+          </ThemeProvider>
+        </QueryClientProvider>
+      </MemoryRouter>
+    );
+  }
+
+  return render(ui, { wrapper: Wrapper, ...renderOptions });
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/types/api.ts b/src/Content/Presentation/Presentation.WebUI/src/types/api.ts
new file mode 100644
index 0000000..b9c53fc
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/types/api.ts
@@ -0,0 +1,18 @@
+export interface AgentInfo {
+  name: string;
+  description?: string;
+}
+
+export interface ConversationMessage {
+  role: 'user' | 'assistant';
+  content: string;
+  timestamp: string;
+}
+
+export interface ConversationRecord {
+  id: string;
+  userId: string;
+  messages: ConversationMessage[];
+  createdAt: string;
+  updatedAt: string;
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/src/types/signalr.ts b/src/Content/Presentation/Presentation.WebUI/src/types/signalr.ts
new file mode 100644
index 0000000..9f495e4
--- /dev/null
+++ b/src/Content/Presentation/Presentation.WebUI/src/types/signalr.ts
@@ -0,0 +1,14 @@
+export interface SpanData {
+  name: string;
+  traceId: string;
+  spanId: string;
+  parentSpanId: string | null;
+  conversationId: string | null;
+  startTime: string;
+  durationMs: number;
+  status: 'unset' | 'ok' | 'error';
+  statusDescription?: string;
+  kind: string;
+  sourceName: string;
+  tags: Record<string, string>;
+}
diff --git a/src/Content/Presentation/Presentation.WebUI/tsconfig.app.json b/src/Content/Presentation/Presentation.WebUI/tsconfig.app.json
index d00e942..5ac9d4b 100644
--- a/src/Content/Presentation/Presentation.WebUI/tsconfig.app.json
+++ b/src/Content/Presentation/Presentation.WebUI/tsconfig.app.json
@@ -28,5 +28,5 @@
     }
   },
   "include": ["src"],
-  "exclude": ["src/test"]
+  "exclude": ["src/test", "src/__tests__"]
 }
