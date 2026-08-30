public partial class Orders { protected void Page_Load(object sender, EventArgs e) { Session["OrderId"] = ViewState["OrderId"]; } }
