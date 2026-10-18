<#import "template.ftl" as layout>
<@layout.emailLayout>
<@layout.action title=msg("fsPasswordResetTitle") text=msg("fsPasswordResetText") button=msg("fsPasswordResetButton")
  link=link expiry=msg("fsLinkExpires", linkExpirationFormatter(linkExpiration)) ignore=msg("fsPasswordResetIgnore")/>
</@layout.emailLayout>
