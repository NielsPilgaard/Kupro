<#import "template.ftl" as layout>
<@layout.emailLayout>
<@layout.action title=msg("fsVerifyTitle") text=msg("fsVerifyText") button=msg("fsVerifyButton")
  link=link expiry=msg("fsLinkExpires", linkExpirationFormatter(linkExpiration)) ignore=msg("fsVerifyIgnore")/>
</@layout.emailLayout>
