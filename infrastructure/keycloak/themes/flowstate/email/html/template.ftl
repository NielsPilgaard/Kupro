<#--
  Layout for every Keycloak email, including the ones still on Keycloak's own body text.
  Email clients ignore CSS variables and web fonts, so the light-theme values of web/src/styles/tokens.css are inlined
  here: --bg #f2f4f3, --panel #ffffff, --line #d3dbd8, --ink #16201d, --mute #56655f, --acc #2c7a6a, --on-acc #ffffff.
  The mark is drawn with three rounded cells, because Gmail and Outlook don't show SVG.
-->
<#macro emailLayout>
<html lang="${locale.language}" dir="${(ltr)?then('ltr','rtl')}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light">
</head>
<body style="margin:0;padding:0;background:#f2f4f3;">
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f2f4f3;">
<tr><td align="center" style="padding:32px 16px;">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;">
    <tr><td style="padding:0 0 20px 4px;">
      <table role="presentation" cellpadding="0" cellspacing="0"><tr>
        <td style="width:24px;padding-right:10px;" aria-hidden="true">
          <div style="width:13px;height:4px;border-radius:2px;background:#16201d;margin:0 0 2px 2px;"></div>
          <div style="width:15px;height:4px;border-radius:2px;background:#2c7a6a;margin:0 0 2px 7px;"></div>
          <div style="width:9px;height:4px;border-radius:2px;background:#16201d;opacity:.5;margin:0 0 0 4px;"></div>
        </td>
        <td style="font-family:'Bricolage Grotesque',system-ui,-apple-system,'Segoe UI',sans-serif;font-size:22px;font-weight:700;letter-spacing:-0.01em;color:#16201d;">flowstate</td>
      </tr></table>
    </td></tr>
    <tr><td style="background:#ffffff;border:1px solid #d3dbd8;border-radius:7px;padding:32px;font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;font-size:15px;line-height:1.55;color:#16201d;">
      <#nested>
    </td></tr>
    <tr><td style="padding:16px 4px 0;font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;font-size:12px;color:#56655f;">
      ${msg("fsFooter")}
    </td></tr>
  </table>
</td></tr>
</table>
</body>
</html>
</#macro>

<#-- One action per email: a title, what happened, one button, when the link expires, and what to do if it wasn't you. -->
<#macro action title text button link expiry ignore>
<h1 style="margin:0 0 12px;font-size:19px;font-weight:600;color:#16201d;">${title}</h1>
<p style="margin:0 0 24px;">${text}</p>
<table role="presentation" cellpadding="0" cellspacing="0"><tr>
  <td style="border-radius:5px;background:#2c7a6a;">
    <a href="${link}" style="display:inline-block;padding:11px 20px;font-weight:600;color:#ffffff;text-decoration:none;border-radius:5px;">${button}</a>
  </td>
</tr></table>
<p style="margin:24px 0 0;font-size:13px;color:#56655f;">${expiry}</p>
<p style="margin:8px 0 0;font-size:13px;color:#56655f;">${ignore}</p>
<p style="margin:16px 0 0;font-size:12px;color:#56655f;word-break:break-all;">${msg("fsLinkFallback")}<br><a href="${link}" style="color:#2c7a6a;">${link}</a></p>
</#macro>
