public class PlayerState
{
    public bool isLocal;    // 是否本地玩家(区分敌我手牌显示)
    public PlayerState(bool isLocal) { this.isLocal = isLocal; }
}
