<#outputformat "plainText">
<#assign requiredActionsText><#if requiredActions??><#list requiredActions><#items as reqActionItem>${msg("requiredAction.${reqActionItem}")}<#sep>, </#sep></#items></#list></#if></#assign>
</#outputformat>

<#import "template.ftl" as layout>
<@layout.emailLayout>
<@layout.action title=msg("fsActionsTitle") text=msg("fsActionsText", requiredActionsText) button=msg("fsActionsButton")
  link=link expiry=msg("fsLinkExpires", linkExpirationFormatter(linkExpiration)) ignore=msg("fsActionsIgnore")/>
</@layout.emailLayout>
